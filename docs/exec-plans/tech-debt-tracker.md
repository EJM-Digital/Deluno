# Technical Debt Tracker

Updated: 2026-04-30

Use this file for cleanup work that should not depend on chat handover context.

## Open

- Persist normalized download-client telemetry snapshots and expose last-known data to Dashboard, Queue, Activity, Health, and Imports.
- Add local boot/health scripts that produce agent-readable backend/frontend logs.
- Expand architecture validation beyond high-signal project-reference checks.
- Replace remaining duplicated frontend queue status literals with `apps/web/src/lib/download-telemetry.ts`.
- Keep docs free of old `C:\Users\User\Deluno` workspace paths.

## Closed

- Added compact agent map and knowledge-base validation.
