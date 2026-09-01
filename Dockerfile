# syntax=docker/dockerfile:1.7

# Build the React application separately so the ASP.NET host publishes the
# exact static assets that the checked-in Windows and source workflows use.
FROM node:24-bookworm-slim AS web-build

WORKDIR /src
COPY package.json package-lock.json ./
COPY apps/web/package.json apps/web/package.json
COPY sdk/typescript/package.json sdk/typescript/package.json
RUN npm ci --ignore-scripts

COPY apps/web apps/web
RUN npm run build:web

# Restore and publish only the host. The host project references every runtime
# module, while the solution also contains Windows-only tray projects that do
# not belong in a Linux container.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS publish

WORKDIR /src
COPY NuGet.Config global.json Directory.Build.props ./
COPY src src
COPY --from=web-build /src/apps/web/dist apps/web/dist

RUN dotnet restore src/Deluno.Host/Deluno.Host.csproj
RUN dotnet publish src/Deluno.Host/Deluno.Host.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

# Keep the runtime image small, but include the native tools used for media
# probing and subtitle timing. FfmpegTools resolves these from /usr/bin when
# DELUNO_FFMPEG_DIR is set below.
# .NET 10 images are published on Ubuntu rather than Debian, so do not use
# the retired bookworm-slim tag here. Noble retains apt for FFmpeg installation.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime

RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl ffmpeg \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=publish /app/publish ./

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    Server__Port=8080 \
    Server__AllowLan=true \
    Storage__DataRoot=/data \
    DELUNO_FFMPEG_DIR=/usr/bin

RUN mkdir --parents /data \
    && chown --recursive app:app /app /data

USER app
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=45s --retries=3 \
    CMD curl --fail --silent http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["dotnet", "Deluno.Host.dll"]
