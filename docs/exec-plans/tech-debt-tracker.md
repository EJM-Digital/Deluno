# Technical Debt Tracker

Updated: 2026-05-13

Use this file for cleanup work that should not depend on chat handover context.

## Open

- Broaden SignalR import/recovery events and deepen exact client-item import outcome records.
- Expand per-client history adapters and mark queue-derived history distinctly.
- Expand architecture validation beyond high-signal project-reference checks.
- Break up oversized endpoint registration surfaces, especially `PlatformEndpointRouteBuilderExtensions`.
- Resolve ownership of in-flight `Deluno.Library` and `Deluno.Search` seams.
- Keep docs free of old `C:\Users\User\Deluno` workspace paths.

## Closed

- Added compact agent map and knowledge-base validation.
- Added local boot/health script that produces backend/frontend URLs, process ids, health status, and logs.
- Added validation for duplicated frontend download telemetry status literals and replaced the client telemetry duplicates in the indexers route.
- Persisted normalized download-client telemetry snapshots, exposed last-known telemetry, and added SignalR telemetry-change revalidation for main client telemetry surfaces.
- Refreshed the core repo maps to match the implemented API and added repo change history covering both commit chronology and subsystem expansion.
