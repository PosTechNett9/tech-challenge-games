using FIAP.CloudGames.Games.Infrastructure.Messaging;
using Microsoft.AspNetCore.Mvc;

namespace FIAP.CloudGames.Games.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController(
        IAuthenticationRequestPublisher authPublisher,
        IAuthenticationResponseCache authCache,
        ILogger<AuthController> logger) : ControllerBase
    {
        private readonly IAuthenticationRequestPublisher _authPublisher = authPublisher;
        private readonly IAuthenticationResponseCache _authCache = authCache;
        private readonly ILogger<AuthController> _logger = logger;

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            try
            {
                _logger.LogInformation("[GAMES-AUTH] Login request for user: {Email}", request.Email);

                var requestId = await _authPublisher.PublishAuthenticationRequestAsync(
                    request.Email,
                    request.Password);

                _logger.LogInformation(
                    "[GAMES-AUTH] Authentication request published: RequestId={RequestId}",
                    requestId);

                var response = await _authCache.WaitForResponseAsync(
                    requestId,
                    TimeSpan.FromSeconds(30));

                if (response == null)
                {
                    _logger.LogWarning(
                        "[GAMES-AUTH] Authentication timeout for RequestId={RequestId}",
                        requestId);

                    return StatusCode(504, new
                    {
                        error = "Authentication timeout",
                        message = "The authentication service did not respond in time"
                    });
                }

                if (!response.Success)
                {
                    _logger.LogWarning(
                        "[GAMES-AUTH] Authentication failed for user: {Email}, RequestId={RequestId}",
                        request.Email,
                        requestId);

                    return Unauthorized(new
                    {
                        error = "Authentication failed",
                        message = response.ErrorMessage
                    });
                }

                _logger.LogInformation(
                    "[GAMES-AUTH] User authenticated successfully: UserId={UserId}, RequestId={RequestId}",
                    response.UserId,
                    requestId);

                return Ok(new
                {
                    success = true,
                    userId = response.UserId,
                    token = response.Token,
                    authenticatedAt = response.RespondedAt
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[GAMES-AUTH] Error during login for user: {Email}", request.Email);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }
    }

    public class LoginRequest
    {
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
}