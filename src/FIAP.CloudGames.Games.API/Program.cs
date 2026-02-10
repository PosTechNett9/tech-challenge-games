using Amazon.SimpleNotificationService;
using Amazon.SQS;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using FIAP.CloudGames.Games.API.Extensions;
using FIAP.CloudGames.Games.API.Middlewares;
using FIAP.CloudGames.Games.Application.Interfaces;
using FIAP.CloudGames.Games.Application.Services;
using FIAP.CloudGames.Games.Domain.Interfaces.Repositories;
using FIAP.CloudGames.Games.Infrastructure.Configuration.Auth;
using FIAP.CloudGames.Games.Infrastructure.Configuration.Search;
using FIAP.CloudGames.Games.Infrastructure.Context;
using FIAP.CloudGames.Games.Infrastructure.Logging;
using FIAP.CloudGames.Games.Infrastructure.Messaging;
using FIAP.CloudGames.Games.Infrastructure.Repositories;
using FIAP.CloudGames.Games.Infrastructure.Search;
using FIAP.CloudGames.Games.Infrastructure.Settings;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using System.Security.Claims;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

#region AWS SDK Configuration

builder.Services.AddDefaultAWSOptions(builder.Configuration.GetAWSOptions());
builder.Services.AddAWSService<IAmazonSQS>();
builder.Services.AddAWSService<IAmazonSimpleNotificationService>();

#endregion

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddCustomSwagger();

builder.Services.AddDbContext<GamesDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("ServiceName", "PaymentsService")
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .WriteTo.Console()
    .WriteTo.Seq("http://localhost:5341")
    .CreateLogger();

builder.Host.UseSerilog();

#region Telemetry Configuration
builder.Services.AddOpenTelemetry()
    .WithTracing(tracerProviderBuilder =>
    {
        tracerProviderBuilder
            .AddAspNetCoreInstrumentation(options =>
            {
                options.RecordException = true;
            })
            .AddHttpClientInstrumentation()
            .SetResourceBuilder(
                ResourceBuilder.CreateDefault()
                    .AddService(
                        serviceName: "games-service",
                        serviceVersion: "1.0.0"))
                    .AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri("http://localhost:4317");
                        options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
                    });
    });
#endregion  

#region Application Services Configuration

builder.Services.AddSingleton<IAuthenticationRequestPublisher, AuthenticationRequestPublisher>();
builder.Services.AddSingleton<IPaymentEventPublisher, PaymentEventPublisher>();
builder.Services.AddSingleton<IAuthenticationResponseCache, AuthenticationResponseCache>();
builder.Services.AddScoped<IGameRepository, GameRepository>();
builder.Services.AddScoped<IGameService, GameService>();

builder.Services.AddHostedService<AuthenticationResponseConsumer>();

builder.Services.AddHttpClient();
builder.Services.AddControllers(); 

#endregion

#region JWT Authentication Configuration

builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection("JwtSettings"));

var jwtSettings = builder.Configuration.GetSection("JwtSettings").Get<JwtSettings>();
if (jwtSettings == null || string.IsNullOrEmpty(jwtSettings.Secret))
    throw new InvalidOperationException("JWT settings are not configured properly. Please check your appsettings.json.");

var key = Encoding.UTF8.GetBytes(jwtSettings.Secret);

builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer("Bearer", opt =>
    {
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidAudience = jwtSettings.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            RoleClaimType = ClaimTypes.Role
        };
    });

#endregion

builder.Services.Configure<ElasticsearchSettings>(
    builder.Configuration.GetSection("Elasticsearch"));

builder.Services.AddSingleton(sp =>
{
    var settings = builder.Configuration
        .GetSection("Elasticsearch")
        .Get<ElasticsearchSettings>() ?? new ElasticsearchSettings();

    var clientSettings = new ElasticsearchClientSettings(new Uri(settings.Url));
    return new ElasticsearchClient(clientSettings);
});

builder.Services.AddSingleton(sp =>
{
    return builder.Configuration
        .GetSection("Elasticsearch")
        .Get<ElasticsearchSettings>() ?? new ElasticsearchSettings();
});

builder.Services.AddScoped<IGameSearchService, GameSearchService>();

builder.Services.AddSingleton<ElasticsearchClient>(sp =>
{
    var settings = sp.GetRequiredService<IOptions<ElasticsearchSettings>>().Value;

    var uri = new Uri(settings.Url);
    var clientSettings = new ElasticsearchClientSettings(uri)
        .DisableDirectStreaming();

    return new ElasticsearchClient(clientSettings);
});

builder.Services.AddScoped<IGameSearchService, GameSearchService>();

builder.Services.Configure<PaymentsSettings>(
    builder.Configuration.GetSection("Payments"));

builder.Services.AddHttpClient("Payments", (sp, client) =>
{
    var settings = sp.GetRequiredService<IOptions<PaymentsSettings>>().Value;
    client.BaseAddress = new Uri(settings.BaseUrl);
});

builder.Services.AddHealthChecks()
    .AddCheck("sqs-connection", () =>
    {
        try
        {
            var sqs = builder.Services.BuildServiceProvider().GetRequiredService<IAmazonSQS>();
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy();
        }
        catch
        {
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy();
        }
    });

var app = builder.Build();

app.UsePathBase("/games");
app.UseRouting();

app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseMiddleware<CorrelationIdMiddleware>();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<GamesDbContext>();
    db.Database.Migrate();
}

app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
    options.GetLevel = (httpContext, elapsed, ex) => ex != null
        ? LogEventLevel.Error
        : httpContext.Response.StatusCode > 499
            ? LogEventLevel.Error
            : LogEventLevel.Information;

    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
        diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
        diagnosticContext.Set("UserAgent", httpContext.Request.Headers["User-Agent"].ToString());
    };
});


app.MapGet("/health", () => Results.Ok("OK")).AllowAnonymous();
app.MapGet("/health/ready", () => Results.Ok("OK")).AllowAnonymous();

if (app.Environment.IsDevelopment())
{
    var swaggerBasePath = builder.Configuration["SwaggerBasePath"] ?? "/games";

    app.UseSwagger(c =>
    {
        c.PreSerializeFilters.Add((swagger, req) =>
        {
            swagger.Servers = new List<OpenApiServer>
            {
                new OpenApiServer { Url = swaggerBasePath }
            };
        });
    });

    app.UseSwaggerUI(c =>
    {
        c.RoutePrefix = "swagger";
        c.SwaggerEndpoint("v1/swagger.json", "Games API v1");
    });
}

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false // Apenas verifica se a aplicação está rodando
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready") // Verifica dependências
});


app.UseHttpsRedirection();
app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
try
{
    Log.Information("Starting GamersService application");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
