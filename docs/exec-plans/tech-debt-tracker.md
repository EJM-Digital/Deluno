# Technical Debt Tracker

Updated: 2026-08-18

Use this file for cleanup work that should not depend on chat handover context.

Items marked with a doc reference have measured evidence and a design behind
them; the doc is the source of truth and this list is the index.

## Open — realtime (`ADR-002-realtime-architecture.md`)

- Restore SignalR negotiation. The client sets `skipNegotiation: true` with a
  forced WebSockets transport, which disables the WS → SSE → long-poll fallback
  entirely. Behind a reverse proxy that will not upgrade, the app has no
  realtime and no degraded mode.
- Wire the five orphaned events. The backend publishes `DispatchDetected`,
  `DispatchGrabAttempt`, `DispatchGrabCompleted`, `DispatchImportStarted` and
  `DispatchImportCompleted`; `use-signalr.tsx` registers handlers for eight of
  thirteen names, so these reach nobody.
- Add sequence numbers and a resume window to the realtime envelope. The
  publisher's bounded channel is `DropOldest`, so events are discarded under
  load with no way for a client to detect the gap; `onreconnected` only updates
  a status badge and never refetches. Until this exists, subscribing instead of
  polling is *less* correct than polling.
- Add entity change events — `MovieChanged`, `SeriesChanged`, `LibraryChanged`,
  `SettingsChanged`, `QualityProfileChanged`, `PolicySetChanged`,
  `IntakeSourceChanged`, `AutomationStateChanged`. Thirteen of the dashboard's
  seventeen data sources have no event of any kind today.
- Replace `Clients.All` with per-screen groups.
- Move the frontend to an entity-keyed cache that hydrates from REST and
  invalidates on change events, instead of react-router loaders on intervals.
- Add a resume test that kills the connection mid-stream, replays, and asserts
  the client converged. A silent resume bug is worse than polling.

## Open — scheduling and contention (`AUDIT-001-scheduling-and-contention.md`)

- Gate every frontend interval on `document.visibilityState`. Nothing stops when
  the tab is hidden; measured 204 requests/min and 27.7 MB/hr from one idle
  dashboard tab on a dev-sized library.
- Paginate `/api/movies` and `/api/series`. Both return the full catalogue, so
  the two largest dashboard responses scale linearly with the user's library.
- Lease a batch per tick with bounded concurrency instead of one job per tick.
  The import lane's 2s timer caps sustained throughput at 30 jobs/min regardless
  of machine capacity; a 1,000-item backlog takes 33 minutes of pure tick
  latency. Fits ADR-001 Step 4.
- Make `LeaseNextAsync` use an explicit `IMMEDIATE` transaction. It is safe today
  only because `RecoverExpiredLeasesAsync` happens to issue a write first;
  reorder those statements and the deferred-transaction upgrade starts throwing
  `SQLITE_BUSY` under contention.
- Move the cleanup/retry throttles out of worker instance fields. `_lastDispatchCleanupUtc`
  and friends make "every 6 hours" mean "every 6 hours of uptime", and two hosts
  would each run every pass.

## Open — module split (`ADR-001-module-boundaries.md`, `PLAN-module-split.md`)

- Finish Step 1: extract `Deluno.Quality`, `Deluno.Connections` and
  `Deluno.Libraries`. Security, Notifications and Intake are done; Platform is
  down from 16,017 to 11,864 LOC.
- Create `tests/Deluno.Series.Tests`. 6,629 LOC with no test project of its own,
  and a hard precondition for Step 2 — merging two engines when only one is
  tested is how a silent regression ships.
- Step 2: collapse the fourteen duplicated repository methods into `Deluno.Media`.
- Step 3: give recovery and cleanup a module; they currently span 9 of 12 projects.
- Step 4: split `DelunoHeartbeatWorker` (2,029 LOC, one switch) into handlers
  registered by job type.

## Open — arbitrary caps and hard limits

Goal: nothing the tool does should be capped by a number somebody typed once.

Absolute limitlessness is not a thing anyone can ship — RAM and disk are finite
and indexers rate-limit us whether we like it or not. But most of what looks
like a limit here is not one. The achievable version, and the standard every
item below is measured against:

1. **No arbitrary caps.** If a number is not derived from a real resource
   constraint, it goes.
2. **No silent truncation.** If a limit is applied, the response says so. A
   caller must never be unable to tell it got a partial answer.
3. **Real limits are explicit, configurable and observable** — surfaced in
   settings, not buried in a `Math.Clamp`.
4. **Fan-out is parallel and unbounded by default**, bounded only by
   configured concurrency.

### Caps that silently change behaviour — highest priority

- **Import lane processes 30 jobs/min maximum**, one per 2s tick — see
  `AUDIT-001`. A capacity cap imposed by a timer.
- **Realtime events are dropped at 1,000 queued** (`DropOldest`). Acceptable
  only once resume exists (`ADR-002`); until then it is silent data loss.
- **Telemetry history is truncated at 50 / 30 / 30** —
  `DownloadClientTelemetryService.cs:1061,1314,1366`.
- **Decision explanations cap at 12 alternatives** —
  `AcquisitionDecisionPipeline.cs:224`. This one caps only what is *explained*,
  not what is *considered*, so it is a transparency limit rather than a
  behavioural one — but the north star promises every decision is explainable.

### Silent truncation on list endpoints

No endpoint except download dispatches has a pagination protocol. Everything
else clamps and returns a bare array, so a caller asking for more than the cap
receives fewer items with no indication:

- `JobsEndpointRouteBuilderExtensions.cs` — 100, 200, 500, 100, 100, 100
- `MonitoringService.cs:61` and `MonitoringEndpointRouteBuilderExtensions.cs:56` — 500
- `IntakeEndpointRouteBuilderExtensions.cs:303` — 200
- `DownloadClientEndpointRouteBuilderExtensions.cs:22` — `take ?? 30`, unclamped
- `/api/movies`, `/api/series` — no `take` at all, always the full catalogue

`SqliteDownloadDispatchesRepository.cs:324` already does keyset pagination with
`nextPageToken` and `hasMore`. Generalise that shape across every list endpoint
rather than inventing a second one.

### Configuration ceilings a user can hit

- Dashboard metrics window clamped to 365 days — `DashboardMetricsEndpointRouteBuilderExtensions.cs:44`.
  A user cannot ask for two years.
- Sync interval clamped to 168 hours — `DelunoValueNormalizers.cs:60`. A list
  cannot be synced less often than weekly.
- Backup retention clamped to 100 — `DelunoBackupService.cs:53,362`.
- List-exclusion duration clamped to 3,650 days — `SqliteIntakeRepository.cs:106`.
- Retry attempts clamped to 5, circuit failure threshold to 20 —
  `IntegrationResiliencePolicy.cs:155,156`.
- Ranking model boost clamped to 60, training samples to 5,000 —
  `BoundedReleaseRankingModelService.cs:53`, `MlNetReleaseRankingModelService.cs:113`.
- Monitoring thresholds clamped to 40% storage / 90% failure rate —
  `MonitoringService.cs:256,257`.
- Worker reads a fixed 600 jobs per maintenance tick to plan metadata refresh.

Each needs a decision: raise it, make it configurable, or keep it and document
why. The point is that none of them is currently a decision — they are defaults
that hardened into limits.

### Structural ceilings to make explicit

- **SQLite allows one writer per database file** — note *per file*. Deluno runs
  five (`platform`, `movies`, `series`, `jobs`, `cache`), so there are already
  five independent writers, and WAL means readers never block them. This was
  previously written up as a global ceiling; it is not one, and it was not the
  bottleneck — `Cache=Shared` was. Remaining headroom, in order of value:
  split the highest-churn tables into their own file (a `dispatches.db` would
  take the heaviest writes off `jobs.db`), batch writes into fewer transactions,
  and open read paths with `Mode=ReadOnly`. Worth measuring and stating a real
  number rather than assuming.
- **One job leased per lane tick, three fixed lanes.** Concurrency is a
  consequence of the loop shape rather than a setting.
- **`Clients.All` broadcast** — every event to every connection, so realtime
  cost grows with tabs times events.

## Open — production readiness

- No rate limiting on the API, which issues scoped API keys.
- No API versioning on a surface documented for external integration.
- Multi-instance is half-believed: a `worker_heartbeats` table implies multiple
  workers, but the worker's throttles are per-process fields.
- SQLite's single writer is the scaling ceiling. Fine for the single local
  control plane in `PRODUCT_NORTH_STAR.md`; it should be a stated limit rather
  than an accident.
- Branch protection on `main` has two required checks that recent pushes have
  bypassed. Decide whether it is enforced or remove it.

## Open — older items

- Broaden SignalR import/recovery events and deepen exact client-item import outcome records.
- Expand per-client history adapters and mark queue-derived history distinctly.
- Expand architecture validation beyond high-signal project-reference checks.
- Break up oversized endpoint registration surfaces, especially `PlatformEndpointRouteBuilderExtensions`.
  (Partly addressed: 4,033 → 3,281 LOC as Security, Notifications and Intake moved out.)
- Resolve ownership of in-flight `Deluno.Library` and `Deluno.Search` seams.
- Keep docs free of old `C:\Users\User\Deluno` workspace paths.

## Closed — fixed this session

- **SQLite connections were serialised behind `Cache=Shared`.** Shared cache
  imposes table-level locks inside the process and raises `SQLITE_LOCKED`, which
  `busy_timeout` does not retry; nothing here used an in-memory database, the one
  case it exists for. Switched to private cache and tuned `cache_size` (16 MiB),
  `mmap_size` (256 MiB), `wal_autocheckpoint` (2000 pages), and raised the command
  timeout above `busy_timeout` so the two stopped racing. Measured at 24 concurrent
  workers over the dashboard's endpoints: throughput 2,190 -> 3,932 req/s, p99
  164 -> 22 ms, max 319 -> 24 ms. `a6a1ef8`
- **Search queried only 4 indexers, serially.** `.Take(4)` removed and the fan-out
  parallelised, bounded at 16 in flight. Every configured indexer is now searched,
  and latency is the slowest indexer rather than the sum. `8f86234`
- **The worker paid for every tick before checking whether automation was on.**
  Gate moved first, settings snapshot shared across lanes behind a one-second
  window, heartbeat throttled to 15s, and the ten job-processing services resolved
  only after a job is leased. Idle heartbeat writes ~39/min -> 6/min. `dbd24e0`
- **Automation state was rewritten with unchanged values.** Both writers now
  compare before writing. 15/min -> ~2/min. `8508d88`
- Removed the 63 `IPlatformSettingsRepository` injections that existed only to feed
  the auth check, including a dead per-request resolution in the host middleware.
- Lifted shared SQLite plumbing (`SqliteRecordHelpers`) and domain normalisers
  (`DelunoValueNormalizers`) out of the Platform god-repository.

## Closed

- Added compact agent map and knowledge-base validation.
- Added local boot/health script that produces backend/frontend URLs, process ids, health status, and logs.
- Added validation for duplicated frontend download telemetry status literals and replaced the client telemetry duplicates in the indexers route.
- Persisted normalized download-client telemetry snapshots, exposed last-known telemetry, and added SignalR telemetry-change revalidation for main client telemetry surfaces.
- Refreshed the core repo maps to match the implemented API and added repo change history covering both commit chronology and subsystem expansion.
