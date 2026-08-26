# Deluno

**One app for your movie and TV library, instead of seven.**

Deluno finds releases, sends them to your download client, cleans up what arrives, and files it into your library — then keeps watching for the things you are still missing and the ones worth upgrading.

It is built to replace the whole workflow currently split across Radarr, Sonarr, Prowlarr, Huntarr, Cleanuparr, Recyclarr, Upgradarr and Trash Guides — without collecting their settings screens into one larger settings screen. Where those tools make you configure a pipeline, Deluno asks what you want and decides the rest, then shows you exactly what it did.

Windows and Docker. Single user. Your data stays on your machine.

---

<img src="screenshots/dashboard.png" width="49%"> <img src="screenshots/movies.png" width="49%">

*Overview dashboard · Movies library*

<img src="screenshots/shows.png" width="49%"> <img src="screenshots/queue.png" width="49%">

*TV shows · Download queue*

<img src="screenshots/quality.png" width="49%"> <img src="screenshots/indexers.png" width="49%">

*Quality profiles · Sources and clients*

<img src="screenshots/activity.png">

*Activity — every search, grab, import and rename, in the order it happened*

---

## What it does

**Finds things.** Add Torznab, Newznab or RSS sources directly — there is no Jackett or Prowlarr in the middle. Deluno queries them itself, respects each site's request limits, and routes each library to the sources you chose for it.

**Decides what is worth having.** Quality profiles with cutoffs, custom format scoring, size rules and release preferences. Deluno explains every decision it makes in plain words rather than leaving you to reverse-engineer a score.

**Hands off to your download client.** SABnzbd, NZBGet, qBittorrent, Transmission, Deluge or uTorrent. The client does the transfer work it is good at; Deluno owns search, dispatch, monitoring, import, naming, routing and recovery.

**Cleans up before filing.** A library can refine before it imports: the finished download goes to an external processor, and Deluno imports the cleaned output through the same resolver rather than the raw file.

**Keeps looking.** Recurring searches for what is still missing and what is below its cutoff, with the seeding obligations of private sites respected rather than ignored.

**Tells you the truth.** Live download telemetry on the dashboard, a full operational audit trail in Activity, and health checks that answer whether a thing is *usable* rather than merely reachable.

Movies and TV run on separate engines internally, so they never fight over the same download.

## Install

### Windows

Download the installer from [Releases](https://github.com/jampat000/Deluno/releases). Velopack handles installation and in-app updates.

| | |
|--|--|
| Application | `%LocalAppData%\Deluno` |
| Data | `%LocalAppData%\DelunoData` |
| Config | `%LocalAppData%\Deluno\config\deluno.json` |
| Logs | `%LocalAppData%\Deluno\logs` |

Deluno runs at [http://localhost:5099](http://localhost:5099). Updates appear in **System → Updates** and install in the background.

By default Deluno listens on loopback only, so nothing outside the machine can reach it. To open it to your LAN, set `Server:AllowLan`. Do that deliberately: Deluno is a single-user app and its front door is a username and password.

### Docker

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
      - ./data:/data
      - /your/media:/media
      - /your/downloads:/downloads
    restart: unless-stopped
```

```bash
docker compose up -d
```

Open [http://localhost:5099](http://localhost:5099).

Provider credentials belong in an untracked `.env` file beside the compose file, never in the compose file itself and never in the image:

```bash
TMDB_API_KEY=…
MDBLIST_API_KEY=…
```

To update, pull the new image and recreate the container. No in-place updater runs inside a container.

## First run

Deluno walks you through it. In order: create your account, add a library, connect a source, connect a download client. Each rung tells you what it still needs, and the app will not pretend a step is done when it is not.

## Security

- **One account.** Deluno is single-user. There are no roles to misconfigure.
- **Loopback by default.** Opening Deluno to the network is an explicit choice, not the default.
- **Secrets are stored encrypted** and are never returned by the API once saved — an API key you paste in can be replaced, but not read back out.
- **API keys are scoped** to what a caller actually needs, and can be revoked at any time from **System → API**.
- **Live updates authenticate over `?access_token=`.** WebSockets cannot carry an `Authorization` header, so this is the standard browser approach; the token is the signed, expiring session token, not a long-lived key.
- **No analytics and no usage reporting.** Deluno makes outbound calls to the indexers, download clients and metadata providers you configured, and to its update feed — by default the GitHub releases for this repository, which you can change or point elsewhere.

Deluno is meant to run on your own machine or LAN. If you expose it to the internet, put it behind a reverse proxy with TLS and treat it the way you would any other self-hosted admin interface.

## Development

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Node.js 20+](https://nodejs.org).

```bash
npm install
npm run dev:web        # Vite frontend → :5173
```

In a second terminal:

```bash
dotnet run --project src/Deluno.Host   # API → :5099
```

Before pushing:

```bash
npm run ci:check
```

The full suite:

```bash
dotnet test Deluno.slnx --configuration Release
npm run test:web
```

`npm run test:web` runs three browser projects: `chromium` and `mobile` drive the UI against a preview server, and `shipped` drives the critical journeys against `Deluno.Host` serving its own front end — the path that actually ships.

### Real-world media-flow fixtures

These use temporary SQLite databases and temporary media folders; they never touch a configured library or download folder. They exercise movie and TV flows end to end: quality and custom-format decisions, external-client dispatch and telemetry, post-processing, destination routing, import, naming, catalogue updates and recovery safeguards.

```powershell
# Fast focused flow suite
.\scripts\test-real-world-flows.ps1

# The flow suite plus the complete release test suite
.\scripts\test-real-world-flows.ps1 -FullSuite
```

## How it is built

- .NET 10 backend, React 19 front end. The backend serves the front end and runs all background work — there is no separate agent to install or keep alive.
- SQLite, with a separate database per domain (platform, movies, series, jobs, cache, downloader).
- ffmpeg is used for stream probing. It is bundled in the Docker image and the Windows installer; only a from-source run needs it on `PATH`.
- Designed for libraries of 20,000+ items.

## Docs

- [Architecture](./docs/ARCHITECTURE.md)
- [Deployment](./docs/DEPLOYMENT.md)
- [Packaging and releases](./docs/packaging.md)
- [Backup and restore runbook](./docs/backup-restore-runbook.md)
- [Supported reference media flow](./docs/REFERENCE_MEDIA_FLOW.md)
- [Troubleshooting](./docs/TROUBLESHOOTING.md)
- [0.x to 1.x upgrade guide](./docs/upgrade-guide-0x-to-1x.md)
- [Product north star](./docs/PRODUCT_NORTH_STAR.md)
- [Media automation terminology](./docs/MEDIA_AUTOMATION_TERMINOLOGY.md)

## License

MIT
