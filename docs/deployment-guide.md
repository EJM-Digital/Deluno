# Deluno Deployment Guide

Get Deluno running in under 10 minutes. Choose your platform below.

---

## Docker (recommended for Linux / NAS / homelab)

### Quick start with compose.yaml

The repository ships a `compose.yaml` at the project root. Copy it to your server and start it:

```bash
docker compose up -d
```

Default mapping: host port **5099** → container port **8080**.

Data is persisted at `./artifacts/docker/data` on the host. Change the volume mount to your preferred location before first run:

```yaml
volumes:
  - /your/data/path:/data
```

### Using the published image

Replace the `build` block with the registry image once a release is published:

```yaml
services:
  deluno:
    image: ghcr.io/jampat000/deluno:latest
    container_name: deluno
    ports:
      - "5099:8080"
    environment:
      ASPNETCORE_URLS: http://+:8080
      Storage__DataRoot: /data
    volumes:
      - /srv/deluno/data:/data
      - /your/media:/media   # mount your library roots here
    restart: unless-stopped
```

Pull and start:

```bash
docker pull ghcr.io/jampat000/deluno:latest
docker compose up -d
```

### ffprobe in Docker

The base ASP.NET image does not include ffprobe. Install it in the container or use a custom image:

```yaml
environment:
  DELUNO_FFPROBE_PATH: /usr/bin/ffprobe
```

To bundle ffprobe, extend the image:

```dockerfile
FROM ghcr.io/jampat000/deluno:latest
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*
```

---

## Windows (installer + tray app)

### Installing

1. Download `Deluno-Setup-x.y.z.exe` from the [Releases page](https://github.com/jampat000/Deluno/releases).
2. Run the installer (requires admin). It installs to `%ProgramData%\Deluno\bin`.
3. Data is stored at `%ProgramData%\Deluno\data`.
4. The installer registers the Deluno Windows service and places a tray icon in the Start menu.

### Starting

Launch **Deluno** from the Start menu. The tray icon appears in the system tray. Right-click to open the UI, check service status, or exit.

The application listens on port **7879** by default (configured in `deluno.json` — see Configuration Reference below).

### ffprobe on Windows

The installer bundles `ffprobe.exe` alongside the application binary. No extra step is needed. If you want to use a different ffprobe, set:

```
DELUNO_FFPROBE_PATH=C:\ffmpeg\bin\ffprobe.exe
```

as a system environment variable or in the service's environment.

### Running as a Windows service (headless)

The tray binary supports service mode. After installation the service runs automatically. To manage it manually:

```powershell
# Install / re-register the service
Deluno.exe --install-service

# Uninstall the service
Deluno.exe --uninstall-service

# Start / stop via sc
sc start Deluno
sc stop Deluno
```

---

## Linux headless deployment

### Docker (preferred)

Follow the Docker section above. Set `restart: unless-stopped` to survive reboots.

### systemd

1. Publish a self-contained build or download the Linux binary.
2. Create a service unit at `/etc/systemd/system/deluno.service`:

```ini
[Unit]
Description=Deluno media automation
After=network.target

[Service]
Type=simple
User=deluno
Group=deluno
WorkingDirectory=/opt/deluno
ExecStart=/opt/deluno/Deluno.Host
Restart=on-failure
RestartSec=5

Environment=ASPNETCORE_URLS=http://+:5099
Environment=Storage__DataRoot=/var/lib/deluno

[Install]
WantedBy=multi-user.target
```

3. Enable and start:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now deluno
```

4. Check logs:

```bash
journalctl -u deluno -f
```

---

## Configuration Reference

### Environment variables

| Variable | Default | Description |
|---|---|---|
| `ASPNETCORE_URLS` | `http://+:5099` (bare host) / `http://+:8080` (Docker) | Kestrel bind address. Change the port here when not using Docker. Note: the host binary also hard-codes port 5099 via `ListenAnyIP(5099)`; override with this variable. |
| `Storage__DataRoot` | `data/` relative to the binary | Absolute path for the database, backups, and protection keys. Set to a persistent volume path in Docker or a dedicated directory on bare metal. |
| `DELUNO_FFPROBE_PATH` | _(auto-detected)_ | Explicit path to the `ffprobe` (or `ffprobe.exe`) binary. Takes priority over the bundled binary and the system PATH. |

### deluno.json (Windows tray)

The tray app reads `%ProgramData%\Deluno\data\deluno.json`. Create or edit the file to override defaults:

```json
{
  "port": 7879,
  "dataRoot": "C:\\ProgramData\\Deluno\\data"
}
```

| Key | Default | Description |
|---|---|---|
| `port` | `7879` | Port the backend listens on when launched by the tray app. |
| `dataRoot` | `%ProgramData%\Deluno\data` | Data directory used by the tray-managed backend. |

---

## Post-install: first-run setup

1. Open the UI — `http://localhost:5099` (bare host / Linux) or `http://localhost:7879` (Windows tray).
2. The app detects no account exists and opens the **bootstrap** screen.
3. Enter a username, display name, and password. This creates the admin account.
4. After login, work through Settings in order:
   - **Libraries** — add your movie and TV root folders.
   - **Quality Profiles** — configure which resolutions and codecs are acceptable.
   - **Indexers** — add Prowlarr/Jackett or direct Newznab/Torznab indexers with your API keys.
   - **Download Clients** — add qBittorrent, Deluge, SABnzbd, or NZBGet.
5. Once at least one library, one indexer, and one download client are configured, Deluno is ready.

---

## Upgrade procedure

### Docker

```bash
docker pull ghcr.io/jampat000/deluno:latest
docker compose up -d
```

The new container starts against the same data volume. Migrations run automatically on startup.

### Windows installer

1. Create a backup from the UI (Settings > Backup) or via `POST /api/backups`.
2. Run the new `Deluno-Setup-x.y.z.exe`. The installer stops the service, replaces the binaries, and restarts.
3. Verify the tray icon reappears and the UI loads.

### Linux systemd

1. Stop the service: `sudo systemctl stop deluno`
2. Replace the binary in `/opt/deluno/`.
3. Start: `sudo systemctl start deluno`
4. Check logs for any migration errors.

---

## Backup and restore

### Creating a backup

From the UI: **Settings > Backup > Create Backup**.

Via API:

```bash
curl -X POST http://localhost:5099/api/backups \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: <your-api-key>" \
  -d '{"reason": "pre-upgrade"}'
```

### Listing backups

```bash
GET /api/backups
```

### Downloading a backup

```bash
GET /api/backups/{id}/download
```

This returns a zip archive containing the database and settings.

### Restoring from a backup

Upload the zip via the UI (Settings > Backup > Restore) or via the API:

```bash
curl -X POST http://localhost:5099/api/backups/restore \
  -H "X-Api-Key: <your-api-key>" \
  -F "file=@deluno-backup.zip"
```

Restart the service after a restore to ensure all connections are re-established.

### What is backed up

The backup archive includes:
- The SQLite database (catalog, jobs, settings)
- Platform settings

It does **not** back up your media files or download client data.
