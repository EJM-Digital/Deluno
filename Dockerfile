# syntax=docker/dockerfile:1

FROM node:22-bookworm-slim AS web-build
WORKDIR /src

COPY package.json package-lock.json ./
COPY apps/web/package.json ./apps/web/package.json
RUN npm ci

COPY apps/web ./apps/web
RUN npm run build:web

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY NuGet.Config ./
COPY Deluno.slnx ./
COPY Directory.Build.props ./
COPY global.json ./
COPY src/Deluno.Api/Deluno.Api.csproj ./src/Deluno.Api/
COPY src/Deluno.Contracts/Deluno.Contracts.csproj ./src/Deluno.Contracts/
COPY src/Deluno.Filesystem/Deluno.Filesystem.csproj ./src/Deluno.Filesystem/
COPY src/Deluno.Host/Deluno.Host.csproj ./src/Deluno.Host/
COPY src/Deluno.Infrastructure/Deluno.Infrastructure.csproj ./src/Deluno.Infrastructure/
COPY src/Deluno.Integrations/Deluno.Integrations.csproj ./src/Deluno.Integrations/
COPY src/Deluno.Jobs/Deluno.Jobs.csproj ./src/Deluno.Jobs/
COPY src/Deluno.Movies/Deluno.Movies.csproj ./src/Deluno.Movies/
COPY src/Deluno.Platform/Deluno.Platform.csproj ./src/Deluno.Platform/
COPY src/Deluno.Realtime/Deluno.Realtime.csproj ./src/Deluno.Realtime/
COPY src/Deluno.Series/Deluno.Series.csproj ./src/Deluno.Series/
COPY src/Deluno.Worker/Deluno.Worker.csproj ./src/Deluno.Worker/

# Tray app (Windows-only; restore still runs on Linux with EnableWindowsTargeting=true)
COPY apps/windows-tray/Deluno.Tray.csproj ./apps/windows-tray/

# Test projects (restore only; tests are not run in the Docker build)
COPY tests/Deluno.Integrations.Tests/Deluno.Integrations.Tests.csproj ./tests/Deluno.Integrations.Tests/
COPY tests/Deluno.Movies.Tests/Deluno.Movies.Tests.csproj ./tests/Deluno.Movies.Tests/
COPY tests/Deluno.Persistence.Tests/Deluno.Persistence.Tests.csproj ./tests/Deluno.Persistence.Tests/
COPY tests/Deluno.Platform.Tests/Deluno.Platform.Tests.csproj ./tests/Deluno.Platform.Tests/

RUN dotnet restore ./Deluno.slnx

COPY src ./src
COPY --from=web-build /src/apps/web/dist ./apps/web/dist

RUN dotnet publish ./src/Deluno.Host/Deluno.Host.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# ffmpeg/ffprobe for media stream probing and validation
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://+:8080
ENV Storage__DataRoot=/data

EXPOSE 8080
VOLUME ["/data"]

COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "Deluno.Host.dll"]
