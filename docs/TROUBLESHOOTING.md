# Deluno Troubleshooting Guide

Updated: 2026-05-13

This guide focuses on issues users are likely to hit with the deployment paths that actually exist in this repo today:

- Docker
- Docker Compose
- Windows Velopack installer and in-app updates
- local dev host runs

## First Checks

Before debugging anything else, confirm:

1. Deluno is running.
2. You are opening the correct URL.
3. The Deluno process can read and write its configured data root.
4. The library/download paths you configured are visible from Deluno's runtime environment.

Useful health URLs:

- `/health`
- `/api/health/ready`

Examples:

```powershell
curl http://127.0.0.1:5099/health
curl http://127.0.0.1:5099/api/health/ready
```

## Docker

### Issue: The Container Starts But The UI Does Not Load

Checks:

```powershell
docker ps
docker logs deluno
```

If using Compose:

```powershell
docker compose ps
docker compose logs deluno
```

Make sure you are opening the host port, not the container port:

- default host URL from checked-in Compose: `http://127.0.0.1:5099`
- default container port inside Docker: `8080`

### Issue: Deluno Is Running But Libraries Or Downloads Are Missing

Cause:

- the host folders were not mounted into the container
- or Deluno was configured with host paths instead of container paths

Correct pattern:

```yaml
volumes:
  - /srv/media/movies:/media/movies
  - /srv/media/tv:/media/tv
  - /srv/downloads:/downloads
```

Then configure Deluno to use:

```text
/media/movies
/media/tv
/downloads
```

not the host-side `/srv/...` or `D:\...` paths.

### Issue: Data Disappears After Recreate

Cause:

- no persistent `/data` mount

Fix:

```yaml
volumes:
  - ./artifacts/docker/data:/data
```

Deluno stores databases and protection keys under the configured data root.

### Issue: Docker Update Buttons Are Missing

Expected behavior:

- Docker installs do not offer in-place binary apply controls.
- Update by pulling a newer image tag and recreating the container.

### Issue: Download Client Webhooks Never Arrive

Checks:

- the webhook URL must be reachable from the download client
- the download client must target the Deluno host/port that it can actually see
- if both tools run in Docker, use the network-reachable service URL, not a host-only localhost assumption

## Windows

### Issue: Deluno Starts But Nothing Loads

For installed Windows builds, verify Deluno is running from the tray and listening on the expected local port.

Recommended launch:

```powershell
curl http://127.0.0.1:5099/health
```

Then open:

```text
http://127.0.0.1:5099
```

If it still fails:

- check whether another app already uses that port
- restart Deluno from the tray menu
- verify the data root is writable

### Issue: Deluno Cannot Save Data

Cause:

- the configured data root is not writable
- or you are trying to use a protected install directory

Fix:

- use a writable persistent folder such as `%LocalAppData%\DelunoData`
- do not use `Program Files` as the live data root
- Deluno settings are now canonicalized to `%LocalAppData%\Deluno\config\deluno.json`; legacy `%ProgramData%\Deluno\data\deluno.json` is read as fallback and migrated forward automatically

### Issue: In-App Update Is Not Available

Checks:

- open `System > Updates` and confirm `Install kind` is `windows-packaged`
- if install kind is `manual`, reinstall Deluno using the latest setup executable from GitHub Releases
- ensure network access to GitHub release assets

### Issue: Update Downloaded But Not Applied

Checks:

- in `System > Updates`, review `State` and `Restart required`
- use `Restart and apply` when an update is staged
- if apply is blocked, resolve backup path/permission errors first, then retry

### Issue: Update Failed And Rolled Back

Checks:

- relaunch Deluno and confirm active version in `System > Updates`
- verify the latest backup in `System > Backups`
- retry update after confirming network and disk space health

### Issue: Libraries Or Downloads Are Not Found On Windows

Checks:

- verify the exact Windows paths exist
- verify the Deluno process account can access them
- if Deluno runs on Windows and another tool runs in Docker, remember they do not share the same path view

## Local Development

### Issue: `npm run dev:local` Fails

Checks:

- confirm the repo-local .NET SDK exists at `.dotnet/dotnet.exe`
- confirm `npm ci` has been run
- inspect:

```text
.deluno/boot-health.json
.deluno/logs/backend.log
.deluno/logs/backend.err.log
.deluno/logs/frontend.log
.deluno/logs/frontend.err.log
```

### Issue: Backend Is Running But Frontend Is Not

The dev script uses:

- backend: `http://127.0.0.1:5099`
- frontend Vite dev server: `http://127.0.0.1:5173`

Open the URL that matches the mode you are running:

- single host publish/run: backend URL
- `dev:local`: frontend URL for the Vite app, backend URL for direct API checks

## Authentication And Bootstrap

### Issue: Bootstrap Keeps Appearing

Checks:

- make sure Deluno is using the same persistent data root between restarts
- verify the data root mount or folder was not replaced
- confirm the app can write `platform.db`

### Issue: API Calls Return Unauthorized

Checks:

- log in again through the UI
- if using external tools, make sure you created an API key and are sending it correctly
- remember most `/api/*` routes require authentication, while `/health` and bootstrap/login paths do not

## Path Mapping Rules

When debugging path-related behavior, always ask:

1. Where is Deluno actually running?
2. What path does that runtime see?
3. Did I configure Deluno with that runtime-visible path?

Examples:

- Windows app on host: use Windows paths like `D:\Media\Movies`
- Docker app on Linux host: use container paths like `/media/movies`
- Docker app on Windows host: use the container path inside Deluno, not the host `D:\...` path

## When To Rebuild

Rebuild the Docker image when:

- code changed
- frontend assets changed
- .NET dependencies changed

Typical flow:

```powershell
docker compose down
docker compose up --build -d
```

Re-run the Windows publish when:

- backend code changed
- frontend assets changed
- packaging settings changed

Typical flow:

```powershell
.\scripts\publish-windows.ps1
```
