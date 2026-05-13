# Deluno

Deluno is a personal media manager for movies and TV with separate engines, quality-aware acquisition, library routing, and full operational visibility.

## Core capabilities

- Separate movie and TV workflows
- Quality profiles with cutoff behavior
- Upgrade and replacement protection
- Custom format scoring
- Library-specific indexer and download-client routing
- Automated missing and upgrade search cycles
- Import recovery workflows and operational audit history

## Install and run

### Windows (recommended)

Download the latest setup executable from [GitHub Releases](https://github.com/jampat000/Deluno/releases).

Windows uses Velopack for installer and updates:

- Install location: `%LocalAppData%\Deluno`
- Runtime data location: `%LocalAppData%\DelunoData`
- Config location: `%LocalAppData%\Deluno\config\deluno.json`
- In-app updates: `System > Updates`
- Default update channel: `stable`

If you are coming from a legacy/manual run, Deluno can read legacy settings from `%ProgramData%\Deluno\data\deluno.json` and migrate them to the canonical config path.

### Docker

Start with compose:

```bash
docker compose up
```

Docker update model:

- no in-place updater inside containers
- pull a newer image tag and recreate containers
- keep persistent volumes mounted (for example `/data`)
- use runtime paths that exist inside the container (`/media/...`, `/downloads`, `/data`)

### Local development

Requirements:

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org)

Run:

```bash
npm install
npm run dev
```

## Repository maps and docs

- [AGENTS.md](/C:/Users/User/Projects/Deluno/AGENTS.md)
- [docs/README.md](/C:/Users/User/Projects/Deluno/docs/README.md)
- [docs/ARCHITECTURE.md](/C:/Users/User/Projects/Deluno/docs/ARCHITECTURE.md)
- [docs/packaging.md](/C:/Users/User/Projects/Deluno/docs/packaging.md)
- [docs/DEPLOYMENT.md](/C:/Users/User/Projects/Deluno/docs/DEPLOYMENT.md)
- [docs/TROUBLESHOOTING.md](/C:/Users/User/Projects/Deluno/docs/TROUBLESHOOTING.md)

## Tech stack

- Frontend: React 19, React Router v7, TypeScript, Vite
- Backend: ASP.NET Core 10, C#
- Data: SQLite (domain-separated databases)
- Realtime: SignalR
- Tests: xUnit and Playwright
