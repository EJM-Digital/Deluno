# Agent-First Realignment

Status: active
Started: 2026-04-30

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

## Next

- Add a local boot-and-health script that starts backend/frontend, records URLs, and writes agent-readable logs.
- Extend agent validation to detect duplicated queue/status strings outside shared helpers.
- Add persisted telemetry snapshots and SignalR events so app behavior is observable without manual refreshes.
- Add execution-plan templates for large feature work and post-merge cleanup.
