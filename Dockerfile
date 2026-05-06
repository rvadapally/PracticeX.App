# syntax=docker/dockerfile:1.7
#
# PracticeX Command Center API container.
#
# Multi-stage build keeps the runtime image small and excludes the SDK
# toolchain. The build copies the entire backend source tree (the API
# project references Application + Infrastructure + Discovery.Contracts
# and embeds SQL migration files via paths under ../../migrations).
#
# Runtime contract:
#   - Process listens on $PORT (cloud convention). Program.cs binds to
#     0.0.0.0:$PORT when the variable is present.
#   - HTTPS is terminated upstream (Render / Fly / Cloudflare); we only
#     speak HTTP inside the container.
#   - Database connection comes from $ConnectionStrings__PracticeX.
#   - StartupMigrationRunner applies idempotent SQL migrations on first
#     boot, so a fresh managed Postgres self-bootstraps.

ARG DOTNET_VERSION=9.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Copy only the project + sln files first so dotnet restore can be cached.
COPY PracticeX.sln ./
COPY src/PracticeX.Api/PracticeX.Api.csproj                       src/PracticeX.Api/
COPY src/PracticeX.Application/PracticeX.Application.csproj       src/PracticeX.Application/
COPY src/PracticeX.Domain/PracticeX.Domain.csproj                 src/PracticeX.Domain/
COPY src/PracticeX.Infrastructure/PracticeX.Infrastructure.csproj src/PracticeX.Infrastructure/
COPY src/PracticeX.Discovery/PracticeX.Discovery.csproj           src/PracticeX.Discovery/
COPY src/PracticeX.Discovery.Contracts/PracticeX.Discovery.Contracts.csproj src/PracticeX.Discovery.Contracts/
COPY src/PracticeX.Agent.Cli/PracticeX.Agent.Cli.csproj           src/PracticeX.Agent.Cli/
COPY src/PracticeX.Agent.Ui/PracticeX.Agent.Ui.csproj             src/PracticeX.Agent.Ui/

RUN dotnet restore src/PracticeX.Api/PracticeX.Api.csproj

# Now copy everything the API build needs — source + the migrations folder
# (referenced as ..\..\migrations from the .csproj for embedded resources).
COPY src/        src/
COPY migrations/ migrations/

RUN dotnet publish src/PracticeX.Api/PracticeX.Api.csproj \
        -c Release \
        -o /app/publish \
        --no-restore \
        /p:UseAppHost=false


FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app

# globalisation-invariant default keeps the image small; PracticeX uses
# CultureInfo.InvariantCulture for parsing already.
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    PORT=8080

COPY --from=build /app/publish ./
COPY scripts/docker-entrypoint.sh /usr/local/bin/practicex-entrypoint
RUN chmod +x /usr/local/bin/practicex-entrypoint

EXPOSE 8080

# Lightweight in-container probe matches the /api/system/info endpoint
# served by Program.cs. Cloud platforms can also configure their own.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD wget -qO- "http://127.0.0.1:${PORT}/api/system/info" || exit 1

ENTRYPOINT ["/usr/local/bin/practicex-entrypoint"]
