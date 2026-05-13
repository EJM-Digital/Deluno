# Deluno Deployment Guide

Updated: 2026-05-13

This guide covers the deployment paths that match the current repository:

- Windows packaged install and in-app updates (Velopack)
- Docker deployment from source
- Docker Compose deployment from source
- Windows self-contained publish and run (advanced/manual)

For end users, the packaged Windows installer is the primary path.

## Quick Start

### Docker

Build and run from the repo root:

```powershell
docker build -t deluno:local .

docker run --name deluno `
  --rm `
  -p 5099:8080 `
  -e Storage__DataRoot=/data `
  -v ${PWD}/artifacts/docker/data:/data `
  deluno:local
```

Open:

```text
http://127.0.0.1:5099
```

Health checks:

```text
http://127.0.0.1:5099/health
http://127.0.0.1:5099/api/health/ready
```

### Windows

For end users, install from GitHub Releases:

```text
https://github.com/jampat000/Deluno/releases
```

Then:

- run the latest `*Setup*.exe`
- open `http://127.0.0.1:5099`
- use `System > Updates` for update checks/download/apply flow

For advanced/manual source runs, publish and run from the repo root:

```powershell
.\scripts\publish-windows.ps1

$env:Storage__DataRoot = "C:\ProgramData\Deluno"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5099"
.\artifacts\publish\win-x64\Deluno.Host.exe
```

Open:

```text
http://127.0.0.1:5099
```

## Runtime Model

Deluno is one host process that:

- serves the web UI
- serves the API
- exposes health endpoints
- persists all app state under a single configurable data root

The key persistence setting is:

```text
Storage__DataRoot
```

If not set, Deluno uses a `data` folder relative to its content root.

Inside that root, Deluno creates:

- `platform.db`
- `movies.db`
- `series.db`
- `jobs.db`
- `cache.db`
- `protection-keys/`

## Docker Deployment

### Recommended Local Compose Flow

Use the checked-in [compose.yaml](/C:/Users/User/Projects/Deluno/compose.yaml):

```powershell
docker compose up --build -d
docker compose logs -f deluno
```

Default URL:

```text
http://127.0.0.1:5099
```

### Add Media Mounts

For real use, Deluno must be able to see:

- movie libraries
- TV libraries
- download folders
- any refine-before-import staging folders

Example Compose service:

```yaml
services:
  deluno:
    build:
      context: .
      dockerfile: Dockerfile
    image: deluno:dev
    container_name: deluno
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

Important:

- Deluno must be configured with the container-visible paths such as `/media/movies`, not the host paths
- webhook callback URLs must point to a URL your download clients can actually reach

### Docker Upgrade Flow

After code changes:

```powershell
docker compose down
docker compose up --build -d
```

Persistent state remains in the mounted data directory.

## Windows Deployment

### Packaged installer (recommended)

Install from the latest release setup executable:

```text
https://github.com/jampat000/Deluno/releases
```

Packaged defaults:

- app install path: `%LocalAppData%\Deluno`
- runtime data path: `%LocalAppData%\DelunoData`
- config path: `%LocalAppData%\Deluno\config\deluno.json`
- stable update channel with in-app controls under `System > Updates`

### Publish (advanced/manual)

Build the Windows package:

```powershell
.\scripts\publish-windows.ps1
```

Output:

```text
artifacts/publish/win-x64
```

### First Run (advanced/manual publish)

Recommended launch:

```powershell
$env:Storage__DataRoot = "C:\ProgramData\Deluno"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5099"
.\artifacts\publish\win-x64\Deluno.Host.exe
```

Recommended data location:

```text
C:\ProgramData\Deluno
```

Do not use `Program Files` as the writable data root.

### Windows Paths

If Deluno runs directly on Windows, configure libraries and downloads using the real Windows paths it can access, for example:

```text
D:\Media\Movies
D:\Media\TV
D:\Downloads
```

If an external tool runs in Docker while Deluno runs on Windows, do not assume those tools share the same path view. Path mapping must be planned explicitly.

## First-Run Checklist

After the app starts:

1. Open the web UI.
2. Complete bootstrap if no user exists yet.
3. Add or verify libraries.
4. Add indexers and download clients.
5. Test indexers and clients from the UI.
6. Confirm the library and download paths are valid from Deluno's runtime environment.
7. If using refine-before-import or external webhooks, verify callback URLs and shared paths.

## Health And Logs

### Health Endpoints

- `/health`
- `/api/health/ready`
- `/api/system/health` after authentication

### Docker Logs

```powershell
docker compose logs -f deluno
```

or

```powershell
docker logs -f deluno
```

### Windows Logs

If you run the published app from a terminal, standard output is your first log surface.

For dev-mode local runs, the convenience script writes:

- `.deluno/logs/backend.log`
- `.deluno/logs/backend.err.log`
- `.deluno/logs/frontend.log`
- `.deluno/logs/frontend.err.log`

## Environment Settings You Will Actually Use

| Setting | Typical use |
| --- | --- |
| `ASPNETCORE_URLS` | Set the listen URL and port. |
| `Storage__DataRoot` | Choose where Deluno stores databases, keys, and backups. |

Keep this list intentionally small unless more repo-backed configuration points are documented and verified.

## Known Deployment Rules

- Deluno serves the UI and API from the same host process.
- Docker runtime port is `8080` inside the container unless you change `ASPNETCORE_URLS`.
- The default checked-in Compose file maps host `5099` to container `8080`.
- Every path Deluno must read or write must exist in the runtime environment Deluno actually runs inside.
- Public webhook endpoints for download clients should only be exposed in ways you intentionally control.
