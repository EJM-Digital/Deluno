# AUDIT-001 — scheduling, contention and overlapping work

**Status:** findings only, nothing changed
**Date:** 2026-08-18
**Measured against:** `main` @ `1335705`, dev database, app running via
`scripts/start-local-app.ps1`
**Companion to:** `ADR-001-module-boundaries.md` (which contexts own what) and
`PLAN-module-split.md` (Step 4 splits the worker)

ADR-001 is about *where code lives*. This is about *when it runs, and what it
fights with*. The two are related — Step 4 of the plan already calls for
splitting the worker — so the findings below are written to feed that step
rather than to compete with it.

Every number here was measured on the running app, not estimated. Where a
finding is reasoning from code rather than measurement, it says so.

---

## Summary

| # | Finding | Severity | Evidence |
|---|---|---|---|
| 1 | Dashboard polls 17 endpoints every 5s — 204 req/min from one idle tab | **High** | measured |
| 2 | Polling continues while the browser tab is hidden | **High** | code |
| 3 | 13 realtime events are published and the dashboard uses none of them | **High** | code |
| 4 | Worker does ~39–50 DB round trips/min at idle, before checking whether automation is even enabled | **High** | measured |
| 5 | `library_automation_state` rewritten 15×/min with unchanged values | Medium | measured |
| 6 | Import throughput is capped at 30 jobs/min by tick rate, not capacity | **High** | code |
| 7 | Three lanes contend on one `jobs.db` writer | Medium | measured |
| 8 | Cleanup/retry throttles are per-process instance fields | Medium | code |
| 9 | `LeaseNextAsync` relies on an implicit write to avoid a deferred-transaction upgrade | Medium | code |
| 10 | `Cache=Private` is set alongside WAL | Low–Medium | resolved in #143 |

---

## Frontend

### 1. The dashboard asks for everything, every five seconds — measured

`apps/web/src/routes/dashboard-page.tsx:208` revalidates the route loader on a
5-second interval. `dashboardLoader` (line 113) fans out to **17 endpoints**,
sixteen in one `Promise.all` and `/api/dashboard/metrics` after it.

Measured per revalidate against the dev database:

```
/api/movies                        10,359 B
/api/dashboard/metrics?days=30      7,862 B
/api/series                         7,645 B
/api/libraries                      3,124 B
/api/library-automation             2,336 B
/api/policy-sets                    1,850 B
/api/quality-profiles               1,778 B
/api/search-cycles?take=8           1,653 B
/api/settings                       1,481 B
/api/series/wanted                  1,271 B
… 7 more                              981 B
                                   ───────
                            total  40,340 B
```

| | requests | bytes |
|---|---:|---:|
| per revalidate | 17 | 40 KB |
| per minute | **204** | 0.46 MB |
| per hour | **12,240** | 27.7 MB |

That is one browser tab, idle, on a **dev-sized library**. `/api/movies` and
`/api/series` return the full catalogue with no pagination, so the two largest
responses grow linearly with the user's library. A user with 3,000 films rather
than a handful is looking at megabytes per poll.

Three more independent pollers run alongside it: activity at 10s
(`activity-page.tsx:77`), search cycles at 10s (`search-cycles-page.tsx:103`),
notifications at 10s (`useNotifications.ts:167`), plus `use-attention.ts:56` on
a caller-supplied interval. None of them coordinate, and several read the same
underlying tables.

### 2. Nothing stops when the tab is hidden — code

There is no `visibilityState`, `visibilitychange`, or `document.hidden` check
anywhere in `apps/web/src`. Every interval above keeps firing on a backgrounded
tab. A user who leaves the dashboard open on a second monitor overnight
generates ~100k requests by morning.

### 3. The push channel already exists and is unused where it matters — code

`src/Deluno.Realtime` already publishes thirteen events:

```
ActivityEventAdded      DispatchDetected        DispatchGrabAttempt
DispatchGrabCompleted   DispatchImportCompleted DispatchImportStarted
DownloadProgress        HealthChanged           ImportStateChanged
QueueItemAdded          QueueItemRemoved        QueueItemStatusChanged
SearchRunCompleted
```

The web app connects to the hub (`lib/use-signalr.tsx`) and subscribes on
exactly two screens — `connections-screen.tsx` and `system-screen.tsx`. The
dashboard, which is the most-watched screen and by far the heaviest poller,
subscribes to nothing.

This is the single biggest lever in the audit: the infrastructure to stop
polling is already built, wired, and running.

---

## Worker

### 4. Every lane tick pays full price before asking whether it should run — measured

`DelunoHeartbeatWorker.RunLaneAsync` (line 60) runs three lanes concurrently:

| lane | interval | ticks/min |
|---|---:|---:|
| import | 2s | 30 |
| search | 5s | 12 |
| maintenance | 8s | 7.5 |

On **every** tick, before any work is known to exist, the lane:

1. creates a DI scope and resolves **14 services** (lines 71–84),
2. writes a heartbeat row — `jobQueueRepository.HeartbeatAsync` (line 86),
3. reads platform settings — `platformSettingsRepository.GetAsync` (line 88),
   which itself reads both the settings and roots tables,
4. **and only then** checks `settings.AutoStartJobs` and possibly `continue`s
   (line 89).

The gate is the fourth thing that happens, not the first. With automation
switched off the worker still performs two database round trips per lane tick,
forever.

Measured on the idle running app by sampling `worker_heartbeats.last_seen_utc`
every 250 ms for 20 seconds:

```
distinct heartbeat writes:  13 in 20s  ->  ~39/min
```

The timestamps show the three lanes interleaving and colliding:

```
:00 :02 :04 :05 :06 :08 :10 :12 :14 :15 :16 :18 :20
         ^^^         ^^^             ^^^ ^^^
      search      maintenance      search  maintenance
      (5s)          (8s)            (5s)    (8s)
```

`:05` and `:15` are the search lane; `:08` and `:16` are maintenance landing on
the same seconds the import lane already occupies.

### 5. Automation state is rewritten with values that did not change — measured

`SqliteJobStore.PlanLibrarySearchesAsync` calls
`UpsertLibraryAutomationStateAsync` **unconditionally for every library** at the
top of its loop, then frequently writes again via `UpdateAutomationIdleAsync`
for libraries that are idle or have auto-search off. `updated_utc` is set to
`now` either way.

Measured over 20 seconds at idle:

```
distinct library_automation_state rewrites:  5 in 20s  ->  15/min
```

with 3 of 6 rows changing per pass — all to the same new timestamp and
otherwise identical data. That is ~45 row writes per minute that carry no
information.

### 6. Import throughput is capped by the tick rate — code

Each lane leases **one** job per tick (`LeaseNextAsync`, `LIMIT 1`, line 148),
processes it inline, then waits for the next tick. There is no batching and no
concurrency within a lane.

The import lane ticks every 2 seconds, so:

> **maximum sustained import rate = 30 jobs/minute**

regardless of how many imports are queued or how fast the machine is. A backlog
of 1,000 imports takes at least 33 minutes of pure tick latency even if every
import itself is instantaneous. `PeriodicTimer` does not queue missed ticks, so
a slow job does not let the lane catch up afterwards — it simply loses that
tick's slot.

This is the finding most likely to be experienced as "Deluno is slow" by a user
with a real backlog, and it is entirely an artifact of the loop shape.

### 7. Three lanes, one writer — measured

All three lanes lease from the same `job_queue` table in the same `jobs.db`.
SQLite permits one writer at a time per database, so the collisions visible in
finding 4 are three connections queuing behind one write lock.

Observed supporting detail: `jobs.db` is 299 KB but `jobs.db-wal` has a
high-water mark of **4.1 MB** — a WAL 14× the size of its database. WAL space is
reused rather than shrunk, so this is a record of past checkpoint starvation
under load, not an ongoing leak (measured: the file does not grow at idle). It
does show that under write pressure the checkpointer could not get a clean
window.

### 8. Throttles are per-process, not per-installation — code

`_lastDispatchCleanupUtc`, `_lastDispatchRetryPassUtc`, `_lastMetadataAutomationUtc`,
`_lastImportAutomationUtc` and `_lastIntakeAutomationUtc` are instance fields on
the worker (lines 33–37). Consequences:

- the "every 6 hours" cleanup is really "every 6 hours *of uptime*"; a process
  that restarts every few hours may never reach the interval,
- two hosts running simultaneously — `Deluno.Host` and the tray's
  `ServiceHost`/`DelunoServer` both register `AddDelunoWorkerModule` — would each
  keep their own timers and both run the pass,
- the schedule is invisible: it cannot be inspected, logged against, or reset
  without a restart.

Each field is written by exactly one lane, so there is no data race today. That
is a property of the current lane assignment, not something the code enforces.

### 9. The job lease avoids a deferred-transaction upgrade by luck — code

`LeaseNextAsync` opens `BeginTransactionAsync(...)`, which in
Microsoft.Data.Sqlite is **DEFERRED**. A deferred transaction that reads first
and writes second must upgrade its lock, and SQLite returns `SQLITE_BUSY`
immediately on a read→write upgrade rather than honouring `busy_timeout`,
because waiting could deadlock.

The code is safe today only because `RecoverExpiredLeasesAsync` runs as the
first statement inside the transaction and issues an `UPDATE`, which takes the
write lock up front where `busy_timeout` does apply. Reorder those two
statements, or make the recovery pass conditional, and the lease path starts
throwing under contention.

This should be an explicit `BeginTransaction(deferred: false)` so the guarantee
is stated rather than inherited.

### 10. `Cache=Private` alongside WAL — resolved in #143

`SqliteDatabaseConnectionFactory` sets `Cache = SqliteCacheMode.Private` together
with `journal_mode=WAL`, `busy_timeout=5000`, `synchronous=NORMAL`, and a
30-second command timeout.

Private cache is deliberate: shared cache adds table-level locking between
connections in the same process and can surface `SQLITE_LOCKED`, which
`busy_timeout` does **not** retry — it only covers `SQLITE_BUSY`. For file-backed
WAL databases, private cache lets readers and the writer run concurrently.

The #143 write-throughput benchmark exercised all five database files at 1, 2,
4, 8, 16, and 24 concurrent writers, with both single-row and 100-row
transactions. It observed zero `SQLITE_BUSY` and zero `SQLITE_LOCKED`; the
hardware, ranges, and methodology are recorded in `docs/ARCHITECTURE.md`.

---

## What follows from this

The findings split cleanly by cost and risk.

**Cheap, high return, no architectural change:**

- Gate every frontend interval on `document.visibilityState` (finding 2). A few
  lines; removes all polling from backgrounded tabs.
- Move the `AutoStartJobs` check to the top of the lane tick, before the scope
  and the heartbeat (finding 4). The settings snapshot can be cached for a few
  seconds; it changes rarely.
- Make `UpsertLibraryAutomationStateAsync` skip the write when nothing changed
  (finding 5).
- Make the lease transaction explicitly `IMMEDIATE` (finding 9).

**Structural, and already on the roadmap as Step 4:**

- Lease a *batch* per tick and process it with bounded concurrency, instead of
  one job per tick (finding 6). This is the change that removes the 30/min
  ceiling, and it fits naturally into the handler-per-job-type design Step 4
  already describes.
- Give each job type its own handler with its own cadence, rather than three
  fixed lanes on fixed timers whose ticks collide (findings 4 and 7).
- Move the cleanup/retry schedule into the database so it survives restarts and
  cannot double-run across hosts (finding 8).

**Needs a decision, not just an edit:**

- Move the dashboard onto the realtime events that already exist (findings 1
  and 3). This is the largest win available and the one that most changes how
  the frontend is written. Paginating `/api/movies` and `/api/series` matters
  regardless, since those two responses scale with the user's library.
- Retain private cache and the measured SQLite headroom (finding 10, resolved
  in #143).

The SQLite cache finding was resolved in #143. The remaining findings in this
audit were not changed here. ADR-001 Step 1 is mid-flight (Security,
Notifications and Intake are extracted; Quality, Connections and Libraries are
not), and touching the worker while the module split is in progress would
violate the plan's own rule about one concern per commit.
