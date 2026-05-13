# Deluno Packaging Guide

Updated: 2026-05-13

This guide covers the packaging paths that exist in this repository today:

- Docker image build from source
- Docker Compose for local or home-lab deployment
- Windows self-contained publish from source

This repository does not currently ship a polished installer in the checked-in mainline docs. The reliable packaging paths are the source-based ones documented here.

## What Deluno Packages

Deluno is packaged as a single ASP.NET Core host that:

- serves the React frontend
- exposes the `/api/*` surface
- exposes `/health` and `/api/health/ready`
- stores persistent state under one data root

By default, Deluno writes its persistent files under a `data` directory relative to the app root. You can override that with:

```text
Storage__DataRoot
```

Inside that data root, Deluno creates and manages:

- `platform.db`
- `movies.db`
- `series.db`
- `jobs.db`
- `cache.db`
- `protection-keys/`
- backup and restore-staging folders when backup flows are used

## Docker

### Files In This Repo

- [Dockerfile](/C:/Users/User/Projects/Deluno/Dockerfile)
- [compose.yaml](/C:/Users/User/Projects/Deluno/compose.yaml)
- [.dockerignore](/C:/Users/User/Projects/Deluno/.dockerignore)

### What The Docker Image Does

The image:

- builds the web app with Node
- publishes the .NET host
- runs Deluno on port `8080` inside the container
- persists Deluno state under `/data`

Relevant runtime defaults in the image:

```text
ASPNETCORE_URLS=http://+:8080
Storage__DataRoot=/data
```

### Build The Image

From the repo root:

```powershell
docker build -t deluno:local .
```

### Run The Container

Minimal persistent run:

```powershell
docker run --name deluno `
  --rm `
  -p 5099:8080 `
  -e Storage__DataRoot=/data `
  -v ${PWD}/artifacts/docker/data:/data `
  deluno:local
```

Then open:

```text
http://127.0.0.1:5099
```

Health checks:

```text
http://127.0.0.1:5099/health
http://127.0.0.1:5099/api/health/ready
```

### Mount Media Paths For Real Use

If Deluno needs to see your library roots, downloads, or staging folders, mount them into the container and then use the container-visible paths inside Deluno settings.

Example:

```powershell
docker run --name deluno `
  --rm `
  -p 5099:8080 `
  -e Storage__DataRoot=/data `
  -v ${PWD}/artifacts/docker/data:/data `
  -v D:\Media\Movies:/media/movies `
  -v D:\Media\TV:/media/tv `
  -v D:\Downloads:/downloads `
  deluno:local
```

Important rule:

- if Deluno runs in Docker, configure library/download paths using the container paths such as `/media/movies`, `/media/tv`, and `/downloads`, not the host-side `D:\...` paths

### Use Compose

The checked-in [compose.yaml](/C:/Users/User/Projects/Deluno/compose.yaml) builds the image locally and mounts persistent app data:

```powershell
docker compose up --build -d
docker compose logs -f deluno
```

Default access URL:

```text
http://127.0.0.1:5099
```

### Recommended Compose Extension For Real Media Paths

If you want Deluno to access real libraries and downloads, extend `compose.yaml` with your actual mounts:

```yaml
services:
  deluno:
    ports:
      - "5099:8080"
    environment:
      ASPNETCORE_URLS: http://+:8080
      Storage__DataRoot: /data
    volumes:
      - ./artifacts/docker/data:/data
      - /srv/media/movies:/media/movies
      - /srv/media/tv:/media/tv
      - /srv/downloads:/downloads
```

## Windows

### Files In This Repo

- [publish-windows.ps1](/C:/Users/User/Projects/Deluno/scripts/publish-windows.ps1)

### What The Publish Script Does

The script:

- builds the frontend first
- publishes `src/Deluno.Host`
- creates a self-contained Windows build
- enables single-file publish
- enables ReadyToRun
- includes native library self-extract support

Output folder by default:

```text
artifacts/publish/win-x64
```

### Create A Windows Publish

From the repo root:

```powershell
.\scripts\publish-windows.ps1
```

Custom runtime example:

```powershell
.\scripts\publish-windows.ps1 -RuntimeIdentifier win-x64 -Configuration Release
```

### Run The Published App

After publishing, run the generated host executable from the publish folder.

Set a persistent data root before first launch if you do not want to use a relative `data` folder:

```powershell
$env:Storage__DataRoot = "C:\ProgramData\Deluno"
.\artifacts\publish\win-x64\Deluno.Host.exe
```

Then open:

```text
http://localhost:5000
```

If you want Deluno to listen on a different port:

```powershell
$env:ASPNETCORE_URLS = "http://127.0.0.1:5099"
$env:Storage__DataRoot = "C:\ProgramData\Deluno"
.\artifacts\publish\win-x64\Deluno.Host.exe
```

### Recommended Windows Data Location

For a normal Windows install, use a persistent writable folder such as:

```text
C:\ProgramData\Deluno
```

Do not store the live databases inside `Program Files`.

### Optional Windows Service

This repo does not currently include a dedicated Windows service installer script.

If you want service-style startup, publish first and then wrap the published executable with your preferred service manager. Keep the data root outside the install folder.

## Development Convenience Script

For local development, use [start-local-app.ps1](/C:/Users/User/Projects/Deluno/scripts/start-local-app.ps1):

```powershell
npm.cmd run dev:local
```

It:

- starts or reuses the backend on `http://127.0.0.1:5099`
- starts or reuses the frontend dev server on `http://127.0.0.1:5173`
- stores app data under `.deluno/data`
- writes status to `.deluno/boot-health.json`
- writes logs under `.deluno/logs`

This is for development only, not production packaging.

## Current Limits

- The checked-in docs should not assume an official published Docker image unless one is intentionally documented and maintained.
- The checked-in docs should not assume an installer payload beyond the source-based Windows publish.
- If you run Deluno in Docker, every library/download/staging path Deluno must read or write needs a matching volume mount.
