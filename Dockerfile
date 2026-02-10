ARG DOTNET_VERSION=8.0
ARG BUILD_CONFIGURATION=Release

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-alpine AS build
ARG DOTNET_VERSION
ARG BUILD_CONFIGURATION
WORKDIR /src

COPY FIAP.CloudGames.Games.sln ./
COPY src/FIAP.CloudGames.Games.API/FIAP.CloudGames.Games.API.csproj src/FIAP.CloudGames.Games.API/
COPY src/FIAP.CloudGames.Games.Application/FIAP.CloudGames.Games.Application.csproj src/FIAP.CloudGames.Games.Application/
COPY src/FIAP.CloudGames.Games.Domain/FIAP.CloudGames.Games.Domain.csproj src/FIAP.CloudGames.Games.Domain/
COPY src/FIAP.CloudGames.Games.Infrastructure/FIAP.CloudGames.Games.Infrastructure.csproj src/FIAP.CloudGames.Games.Infrastructure/

RUN dotnet restore src/FIAP.CloudGames.Games.API/FIAP.CloudGames.Games.API.csproj
COPY src/ ./src/

RUN dotnet publish src/FIAP.CloudGames.Games.API/FIAP.CloudGames.Games.API.csproj \
    -c ${BUILD_CONFIGURATION} -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-alpine AS runtime
WORKDIR /app

RUN apk add --no-cache icu-libs icu-data-full

ENV ASPNETCORE_URLS="http://+:8080" \
    ASPNETCORE_ENVIRONMENT="Production" \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

EXPOSE 8080

COPY --from=build --chown=app:app /app/publish/ ./

USER app
ENTRYPOINT ["dotnet", "FIAP.CloudGames.Games.API.dll"]