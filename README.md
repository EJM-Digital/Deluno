# Deluno

Deluno is a clean-room media automation app built to deliver one product with fully separated movie and series engines.

The architecture is now locked to:

- `Frontend`: React 19 + React Router v7 + Vite + TypeScript
- `Backend`: ASP.NET Core 10
- `Storage`: SQLite only, split into multiple database files
- `Realtime`: SignalR
- `Workers`: durable hosted background workers

## Core Principles

- one app for users
- hard module boundaries internally
- no shared movie/series business rules
- Windows-first and Docker-first packaging
- no required companion database service

## Repo Layout

- `apps/web`: React frontend shell
- `src/Deluno.Host`: ASP.NET Core host and composition root
- `src/Deluno.Api`: HTTP endpoints
- `src/Deluno.Worker`: worker registration and background services
- `src/Deluno.Contracts`: shared contracts and system manifest
- `src/Deluno.Platform`: platform module
- `src/Deluno.Movies`: movie module
- `src/Deluno.Series`: series module
- `src/Deluno.Jobs`: jobs module
- `src/Deluno.Integrations`: provider and client abstractions
- `src/Deluno.Realtime`: SignalR hub wiring
- `src/Deluno.Filesystem`: filesystem policies and services
- `src/Deluno.Infrastructure`: storage and runtime infrastructure
- `docs`: architecture and strategy docs

## Current State

The architecture is now running end-to-end:

- the ASP.NET Core host serves the bundled React frontend
- the storage bootstrap creates `platform.db`, `movies.db`, `series.db`, `jobs.db`, and `cache.db`
- the movies and series modules each own their own schema and `add/list` API surface
- the first UI slice can add and list movies and series against the live backend

## Local Development

### Prerequisites

- Node.js with `npm`
- the repo-local .NET SDK in `.dotnet/`

### Install frontend dependencies

```powershell
npm.cmd install
```

### Build the frontend bundle

```powershell
npm.cmd run build:web
```

### Build the backend

```powershell
.\.dotnet\dotnet.exe build .\Deluno.slnx
```

### Run the single Deluno host

```powershell
.\.dotnet\dotnet.exe run --project src/Deluno.Host/Deluno.Host.csproj --urls http://127.0.0.1:5099
```

Open `http://127.0.0.1:5099`.

### Optional frontend-only dev server

Run the backend first on `http://127.0.0.1:5099`, then in another shell:

```powershell
npm.cmd run dev --workspace apps/web
```

The Vite dev server proxies `/api` and `/hubs` to the backend host.

## Packaging

- Docker scaffolding: [Dockerfile](C:\Users\User\Documents\Codex\2026-04-21-what-would-it-take-to-build\Dockerfile), [compose.yaml](C:\Users\User\Documents\Codex\2026-04-21-what-would-it-take-to-build\compose.yaml)
- Windows publish script: [scripts/publish-windows.ps1](C:\Users\User\Documents\Codex\2026-04-21-what-would-it-take-to-build\scripts\publish-windows.ps1)
- Packaging notes: [docs/packaging.md](C:\Users\User\Documents\Codex\2026-04-21-what-would-it-take-to-build\docs\packaging.md)

## Next Steps

1. Expand the movie and series schemas toward monitored-state, profiles, and history.
2. Introduce real search/import pipeline job types and SignalR push updates.
3. Add filesystem policy validation around roots, downloads, and imports.
4. Turn the Windows publish output into a proper installer.
