# Technical Debt Tracker

Updated: 2026-08-18

Use this file for cleanup work that should not depend on chat handover context.

**The open items below now live as GitHub issues #130–#143.** Track work there;
this file stays as the index, and the referenced docs hold the evidence and the
design.

Also closed since: batch job leasing and the lane split (#1/#2 of the contention
work) — see `628fa52`.

## Open — realtime (`ADR-002-realtime-architecture.md`)

- Wire the five orphaned events. The backend publishes `DispatchDetected`,
  `DispatchGrabAttempt`, `DispatchGrabCompleted`, `DispatchImportStarted` and
  `DispatchImportCompleted`; `use-signalr.tsx` registers handlers for eight of
  thirteen names, so these reach nobody.
- Add sequence numbers and a resume window to the realtime envelope. The
  publisher's bounded channel is `DropOldest`, so events are discarded under
  load with no way for a client to detect the gap; `onreconnected` only updates
  a status badge and never refetches. Until this exists, subscribing instead of
  polling is *less* correct than polling.
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

## Open — module split (`ADR-001-module-boundaries.md`, `PLAN-module-split.md`)

- Finish Step 1: extract `Deluno.Quality`, `Deluno.Connections` and
  `Deluno.Libraries`. Security, Notifications and Intake are done; Platform is
  down from 16,017 to 11,864 LOC.
- Create `tests/Deluno.Series.Tests`. 6,629 LOC with no test project of its own,
  and a hard precondition for Step 2 — merging two engines when only one is
  tested is how a silent regression ships.
- Step 2: collapse the fourteen duplicated repository methods into `Deluno.Media`.
- Step 3: give recovery and cleanup a module; they currently span 9 of 12 projects.

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

### List pagination protocol

All operational lists now use an explicit, opaque keyset cursor and return
`{ items, nextPageToken, hasMore }`. Catalogue pages retain their first-page
`totalCount` and `facets` alongside the same continuation signal. `pageSize` is
bounded by the shared maximum of 500, so a larger request is visibly partial
when another page remains rather than quietly looking complete.

The coverage includes jobs, activity, decisions, library automation, search
cycles, retry windows, both download-dispatch routes, download health,
monitoring diagnostics, intake-source diagnostics, and the movie/series
catalogues. Callers that need a complete result must intentionally walk the
opaque token; screen windows load one page.

### Configuration ceilings — resolved in #140

- Dashboard metrics accepts a 1–3,650-day window; the response still echoes the
  effective `days` value.
- Sync intervals accept 1–8,760 hours, and the UI offers Fortnightly (336) and
  Monthly (720).
- Backup retention accepts 1–10,000 backups in both save and read normalization.
- List exclusions keep a 3,650-day maximum because ten years covers the real
  finite case; omitting or setting a non-positive duration remains the explicit
  permanent-exclusion path.
- Resilience requests accept up to 20 attempts and a 200-failure circuit
  threshold; these are wide sanity bounds for caller-provided values.
- Ranking configuration accepts a boost of up to 1,000, up to 100,000 minimum
  training samples, and up to 1,000,000 training rows. The SQL source and model
  service use the same 500–1,000,000 row range.
- Monitoring accepts storage thresholds through 95% and failure-rate thresholds
  through 100%. The 5-sample minimum remains because smaller samples make alerts
  statistically noisy.
- Maintenance planning reads `Deluno:Worker:MaintenancePlanningBatchSize`,
  defaulting to 600, so the batch is visible and adjustable rather than hidden
  in the planner.

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
- Resolve ownership of in-flight `Deluno.Library` and `Deluno.Search` seams.
- Keep docs free of old `C:\Users\User\Deluno` workspace paths.

## Closed — fixed this session

- **SignalR forced WebSockets and disabled negotiation, so a reverse proxy that
  would not upgrade left the app with no realtime and no degraded mode.**
  Removed `transport: WebSockets` and `skipNegotiation: true` from
  `use-signalr.tsx`; the client now negotiates and falls back
  WebSockets → SSE → long-polling. `#133`.
- **`ProcessJobAsync` was a 490-line, 14-parameter dispatch chain, and `Deluno.Worker`
  had zero tests.** Split into ten `IJobHandler` implementations under
  `src/Deluno.Worker/Jobs/`, resolved by a `JobHandlerRegistry` keyed on job type. An
  unregistered type now throws instead of returning `"Finished a background task."`
  for nothing. `DelunoHeartbeatWorker.cs` is down from 2,175 to 380 lines; the
  planning methods moved to `WorkPlanner.cs`. `tests/Deluno.Worker.Tests` added.
- **`episode.search` had a handler but no lane would ever lease it.** Added to the
  `search` lane's job types, and `DelunoHeartbeatWorker` now asserts at startup that
  every registered handler's job type is routed by exactly one lane, failing fast
  and naming the offending type if not.
- **The five recurring maintenance passes lived in in-memory fields, so a nightly
  restart reset "every 6 hours" to "never happened" and two hosts sharing one
  database would each run every pass.** Moved to `worker_schedule_state`
  (migration `V0011`) behind `IJobQueueRepository.TryClaimScheduledPassAsync`, an
  atomic `INSERT ... ON CONFLICT ... WHERE` that only one caller within the
  interval can win. `_lastHeartbeatUtc` deliberately stayed per-process — it is
  liveness, not a shared schedule.
- **Lanes had no enable flag and no jitter, so six lanes on 1/2/3/5-second timers
  all started in the same instant.** `JobLane` gained `Enabled` and `Jitter`
  (defaulting to 25% of the lane's interval), applied once before each lane's
  first tick.
- **Jobs were leased one per tick, capping import throughput at 30/min.**
  `LeaseBatchAsync` claims a batch in one transaction (now explicitly IMMEDIATE);
  the worker runs each batch with per-lane concurrency. `628fa52`
- **Three fixed lanes grouped unrelated work.** Split by contended resource —
  planning / import / search / intake / metadata / catalog — so a local catalogue
  recalculation no longer waits behind a metadata refresh blocked on a remote
  provider. `628fa52`
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
- **The platform route and settings repositories still mixed unrelated seams.**
  Added an endpoint inventory snapshot, split route registration into settings /
  setup / tags, migration, library actions, and external integrations, and split
  persistence into settings, download-health, processor, and migration-audit
  repositories. Final files are 304 and 676 lines respectively; the checked-in
  inventory remains unchanged.

## Closed

- Added compact agent map and knowledge-base validation.
- Added local boot/health script that produces backend/frontend URLs, process ids, health status, and logs.
- Added validation for duplicated frontend download telemetry status literals and replaced the client telemetry duplicates in the indexers route.
- Persisted normalized download-client telemetry snapshots, exposed last-known telemetry, and added SignalR telemetry-change revalidation for main client telemetry surfaces.
- Refreshed the core repo maps to match the implemented API and added repo change history covering both commit chronology and subsystem expansion.
