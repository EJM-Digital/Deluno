# Agent-First Realignment

Status: completed
Started: 2026-04-30
Completed: 2026-05-06

## Intent

Align Deluno with the harness-engineering model: humans steer, agents execute, and the repository carries the context, checks, and feedback loops required for reliable agent work.

## Principles

- Keep `AGENTS.md` small and use it as a map.
- Treat `docs/` as the versioned system of record.
- Promote repeated review feedback into tests, scripts, or documentation.
- Make app state, integration state, and validation outputs legible to agents.
- Prefer mechanical constraints over repeated prose reminders.

## Completed

- Added `AGENTS.md` as the compact repo entry point.
- Added `docs/README.md`, `docs/ARCHITECTURE.md`, and `docs/QUALITY_SCORE.md`.
- Added agent-readiness validation script and npm entry point.
- Removed old workspace path links from `README.md`.
- Added `npm run dev:local` to start or reuse backend/frontend dev servers, write `.deluno/boot-health.json`, and collect logs under `.deluno/logs/`.
- Extended `npm run validate:agents` to catch duplicated download telemetry status literals in queue, dashboard, indexer, and telemetry adapter surfaces.
- Added execution-plan templates for large feature work and post-merge cleanup, and made agent validation require them.
- Persisted normalized download-client telemetry snapshots in the cache database, added a last-known endpoint, and wired `DownloadTelemetryChanged` SignalR revalidation for Dashboard, Queue, and Sources.

## Follow-Up Debt

- Broaden SignalR import/recovery events and deepen per-client history adapters.
- Expand architecture validation beyond high-signal project-reference checks.
