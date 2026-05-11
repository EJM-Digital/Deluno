# Deluno Architecture

Deluno is a single-user media automation app with separated movie and TV engines, external service orchestration, and durable local state.

## Module Map

- `Deluno.Host`: composition root, endpoint registration, static frontend hosting.
- `Deluno.Api`: host-level API concerns and readiness.
- `Deluno.Platform`: settings, bootstrap, libraries, quality profiles, tags, API keys, routing.
- `Deluno.Movies`: movie catalog, wanted state, search, grabs, import recovery.
- `Deluno.Series`: series catalog, episode state, search, grabs, import recovery.
- `Deluno.Integrations`: indexer, metadata, and download-client adapters.
- `Deluno.Jobs`: durable queue, activity, search cycle and retry records.
- `Deluno.Filesystem`: import planning, media probing, transfer policy, recovery.
- `Deluno.Realtime`: SignalR events.
- `Deluno.Infrastructure`: storage, resilience, and runtime infrastructure.
- `Deluno.Worker`: hosted background orchestration.
- `Deluno.Contracts`: shared low-level contracts only.

## Boundary Rules

- Movies and Series do not reference each other.
- Feature modules depend on Platform, Jobs, Integrations, Infrastructure, or Contracts as needed; shared behavior should move to one of those modules instead of crossing Movies/Series.
- Host and Worker may compose modules, but should not become domain owners.
- Integrations normalize external protocols before UI or domain modules consume them.
- Frontend routes should consume shared helpers for normalized statuses, capabilities, health, and routing rather than duplicating string logic.
- Persistence schema changes require tests and an update to the relevant map or strategy doc.

## Agent-Legible Invariants

- Status strings used by queues/imports/download clients must have one canonical home.
- Protocol support differences must be encoded as data, not hidden in scattered UI conditionals.
- External boundary payloads must be parsed into typed contracts before business logic uses them.
- Any repeated workflow decision should be captured as a helper, test, or doc entry.

## Validation Hooks

- `npm.cmd run validate:agents` checks documentation and high-signal architecture guardrails.
- `.\\.dotnet\\dotnet.exe test .\\Deluno.slnx --configuration Release` checks backend contracts and persistence behavior.
- `npm.cmd run build:web` checks frontend type and route integrity.
- `npm.cmd run test:web` checks browser smoke coverage.
