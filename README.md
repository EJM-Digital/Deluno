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

The architecture is now running as one authenticated Deluno application:

- the ASP.NET Core host serves the React frontend and the `/api/*` surface
- the storage bootstrap creates `platform.db`, `movies.db`, `series.db`, `jobs.db`, and `cache.db`
- the platform module owns bootstrap, sign-in, settings, libraries, quality profiles, tags, lists, and custom formats
- the movies and series modules each own catalog, wanted state, manual search, history, and import recovery
- SignalR is wired at `/hubs/deluno` for live activity and system updates

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

### Run validation locally

The GitHub Actions workflow in `.github/workflows/ci.yml` runs the same backend and frontend gates on every push and pull request to `main`.

```powershell
.\.dotnet\dotnet.exe restore .\Deluno.slnx
.\.dotnet\dotnet.exe build .\Deluno.slnx --no-restore
.\.dotnet\dotnet.exe test .\Deluno.slnx --no-build
npm.cmd ci
npm.cmd run build:web
npm.cmd run test:web
```

Backend failures are reported by the .NET restore, build, and test steps. Frontend failures are reported by the Vite build and Playwright smoke-test steps, with Playwright reports uploaded as CI artifacts when available.

### Run the single Deluno host

```powershell
.\.dotnet\dotnet.exe run --project src/Deluno.Host/Deluno.Host.csproj --urls http://127.0.0.1:5099
```

Open [http://127.0.0.1:5099](http://127.0.0.1:5099).

### Optional frontend-only dev server

Run the backend first on `http://127.0.0.1:5099`, then in another shell:

```powershell
npm.cmd run dev --workspace apps/web
```

The Vite dev server proxies `/api` and `/hubs` to the backend host.

## Packaging

- Docker scaffolding: [Dockerfile](/C:/Users/User/Deluno/Dockerfile), [compose.yaml](/C:/Users/User/Deluno/compose.yaml)
- Windows publish script: [publish-windows.ps1](/C:/Users/User/Deluno/scripts/publish-windows.ps1)
- Packaging notes: [packaging.md](/C:/Users/User/Deluno/docs/packaging.md)

## Next Steps

1. Deepen movie and TV operational workflows, especially episode-level wanted/import resolution.
2. Replace simulated acquisition behavior with real downstream indexer and download-client integrations.
3. Expand realtime coverage and harden the authenticated single-user flow.
4. Polish and tighten the UI now that the main product surfaces exist.
