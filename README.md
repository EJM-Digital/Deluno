# Deluno

Deluno is a local media library and automation control plane for movies and TV. It is built around how people think about a library: choose what you want, set the experience you want, and see exactly what Deluno is doing.

Deluno is designed to replace the *workflow* currently split across Radarr, Sonarr, Prowlarr, download clients, Huntarr, CleanUpArr, Configarr, and Recyclarr—without copying their complexity into one larger settings screen. Everyday media management lives in the Dashboard; media plans, sources, downloads, cleanup, and automation are configured in Library setup. See the [product north star](./docs/PRODUCT_NORTH_STAR.md).

---

<img src="screenshots/dashboard.png" width="49%"> <img src="screenshots/movies.png" width="49%">

*Overview dashboard · Movies library*

<img src="screenshots/shows.png" width="49%"> <img src="screenshots/queue.png" width="49%">

*TV shows · Download queue*

<img src="screenshots/quality.png" width="49%"> <img src="screenshots/indexers.png" width="49%">

*Quality profiles · Sources and clients*

<img src="screenshots/activity.png">

*Activity log — every import, rename, and job recorded*

---

## What it does

- **Manages indexers directly** — no Jackett or Prowlarr needed. Add Torznab, Newznab, or RSS sources straight in and Deluno queries them itself
- **External download clients** — connect SABnzbd, NZBGet, qBittorrent, Transmission, Deluge, or uTorrent. Deluno orchestrates search, dispatch, monitoring, import, naming, routing, and recovery while the client performs the transfer work.
- Separate movie and TV engines — they never fight over the same downloads
- Quality profiles with cutoff logic and custom format scoring
- Library-routed indexers and download clients — each library can point at different providers
- Automated missing and upgrade search cycles
- Live download telemetry on the dashboard
- Full operational audit trail in Activity
- Guided first-run setup to get from zero to downloading in minutes

## Quick start

### Windows

Download the installer from [Releases](https://github.com/jampat000/Deluno/releases). Velopack handles installation and in-app updates.

| | |
|--|--|
| Install | `%LocalAppData%\Deluno` |
| Data | `%LocalAppData%\DelunoData` |
| Config | `%LocalAppData%\Deluno\config\deluno.json` |

Updates appear in **System → Updates** and install in the background.

### Docker

```yaml
services:
  deluno:
    image: ghcr.io/jampat000/deluno:latest
    ports:
      - "8080:8080"
    volumes:
      - ./data:/data
      - /your/media:/media
      - /your/downloads:/downloads
    restart: unless-stopped
```

```bash
docker compose up -d
```

Open [http://localhost:8080](http://localhost:8080).

To update: pull the new image and recreate the container. No in-place updater runs inside containers.

### Local dev

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Node.js 20+](https://nodejs.org).

```bash
npm install
npm run dev:web        # Vite frontend → :5173
```

In a second terminal:

```bash
dotnet run --project src/Deluno.Host   # API → :5099
```

### Real-world media-flow fixtures

These isolated fixtures use temporary SQLite databases and temporary media folders; they never touch your configured Deluno library or download folders. They exercise movie and TV flows through quality/custom-format decisions, external-client dispatch/telemetry, post-processing, destination routing, import, naming, catalog updates, and recovery safeguards.

```powershell
# Fast focused flow suite
.\scripts\test-real-world-flows.ps1

# The flow suite plus the complete release test suite
.\scripts\test-real-world-flows.ps1 -FullSuite
```

## Notes

- Data is stored in SQLite — separate databases per domain (platform, movies, series, jobs, cache)
- No agent needed; the backend serves the frontend and runs all background work
- ffmpeg is required for stream probing — bundled in the Docker image and in the Windows installer; only needed on PATH if running from source
- SignalR live updates use `?access_token=` for WebSocket auth (standard browser behaviour)

## Docs

- [Architecture](./docs/ARCHITECTURE.md)
- [Deployment](./docs/DEPLOYMENT.md)
- [Packaging and releases](./docs/packaging.md)
- [1.0.0 release notes draft](./docs/release-notes-1.0.0-draft.md)
- [0.x to 1.x upgrade guide](./docs/upgrade-guide-0x-to-1x.md)
- [Backup and restore runbook](./docs/backup-restore-runbook.md)
- [Supported reference media flow](./docs/REFERENCE_MEDIA_FLOW.md)
- [Troubleshooting](./docs/TROUBLESHOOTING.md)
- [Replacement-vision quality gate](./docs/REPLACEMENT_VISION_QUALITY_GATE.md)
- [Media Plan decision proposal](./docs/MEDIA_PLAN_DECISION_PROPOSAL.md)
- [Media automation terminology](./docs/MEDIA_AUTOMATION_TERMINOLOGY.md)

## License

MIT
