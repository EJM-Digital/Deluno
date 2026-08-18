# ADR-001 — Module boundaries

**Status:** accepted, not yet executed (step 0 done)
**Date:** 2026-08-18
**Supersedes:** nothing. This is the first architecture decision recorded for the
project layout.

## The problem, measured

Not an opinion — these are counts from the tree at `3816b1f`.

| Project | LOC | Files |
|---|---:|---:|
| **Deluno.Platform** | **16,017** | 136 |
| Deluno.Integrations | 8,405 | 40 |
| Deluno.Series | 6,640 | 44 |
| Deluno.Jobs | 6,586 | 52 |
| Deluno.Movies | 4,863 | 32 |
| Deluno.Worker | 3,491 | **4** |
| Deluno.Api | 2,793 | 21 |
| Deluno.Filesystem | 2,763 | 10 |
| Deluno.Infrastructure | 937 | 14 |
| Deluno.Realtime | 448 | 4 |
| Deluno.Host | 300 | 2 |
| Deluno.Contracts | 69 | 7 |
| ~~Deluno.Downloader~~ | ~~0~~ | ~~0~~ |

Largest files:

| File | LOC |
|---|---:|
| `Platform/Data/SqlitePlatformSettingsRepository.cs` | 5,519 |
| `Platform/PlatformEndpointRouteBuilderExtensions.cs` | 4,033 |
| `Series/Data/SqliteSeriesCatalogRepository.cs` | 2,685 |
| `Jobs/Data/SqliteJobStore.cs` | 2,625 |
| `Series/SeriesEndpointRouteBuilderExtensions.cs` | 2,396 |
| `Worker/Services/DelunoHeartbeatWorker.cs` | 2,029 |

### 1. `Deluno.Platform` is not a bounded context

It is a third of the C# codebase and holds, in one project: security and
authorisation, users, API keys, indexers, download clients, download-client path
mappings, libraries, library views, library routing, quality profiles, the
quality model, custom formats, destination rules, intake sources and list
exclusions, notifications, processing/processor connections, presets, migration
from Arr apps, and — as of this session — dashboard metrics.

That is at least six contexts. The practical cost is already visible: the
dashboard metrics endpoint **cannot live in Platform**, because Platform sits
underneath Movies and Series and may not reference them. It went to `Deluno.Api`
instead. That is the dependency graph telling us the boundary is wrong.

### 2. Movies and Series are parallel copies, not one engine

Fourteen repository methods exist in both with the same shape:

```
ListAsync            GetByIdAsync            AddAsync
ImportExistingAsync  EnsureWantedStateAsync  DeferWantedSearchAsync
SkipNextWantedSearchAsync  ListEligibleWantedAsync  GetWantedSummaryAsync
GetImportRecoverySummaryAsync  UpdateMetadataAsync  ListSearchHistoryAsync
ListTrackedFilesAsync  GetDailyMetricsAsync
```

Twelve file names mirror exactly (`*DispatchRecoveryHandler`,
`*ImportRecoveryCase`, `*WantedItem`, `*WorkflowService`, `V0001InitialSchema`…).

`GetDailyMetricsAsync` was added to both by copy-paste *in this session*. The
duplication is actively reproducing.

`AGENTS.md` says the engines stay separated internally, and that is right for
domain shape — an episode is not a film. It is not right for wanted-state,
deferral, search history, tracked files and metrics persistence, which are
byte-for-byte the same problem.

### 3. Recovery and cleanup have no home

Grepping for cleanup/recovery concerns finds them spread across projects:

- `cleanup` — **9 of 12** projects
- `import-recovery` — 7 projects
- `seeding` / `retention` — 5 projects
- `stalled` — 2 projects
- `orphan` — 1 project

The north star names recovery and cleanup as first-class: "imports, failed
downloads, stalled or blocked releases, malware-like file patterns, seeding
retention, orphaned download files… belong in the same activity story". They
currently belong to no module at all.

### 4. Series has no test project

`tests/` contains Movies, Integrations, Persistence, Platform and Tray suites.
There is no `Deluno.Series.Tests`, despite `Deluno.Series` being the larger of
the two engines at 6,640 LOC. The persistence suite covers some of it
incidentally; the engine's own behaviour — episode catalogue sync, season
grouping, episode wanted-state — has no dedicated tests. Step 2 merges these two
engines, so this gap has to close *before* that merge, not after.

### 5. One worker, one switch

`DelunoHeartbeatWorker` is 2,029 lines across a 4-file project, dispatching every
job type through a single switch, with three lanes sharing one scope-building
loop and one settings gate.

## Decision

Split along the seams the code already has, in the order below. Each step ships
independently, keeps the suite green, and is revertable on its own.

### Step 0 — remove dead scaffolding ✅ done

`Deluno.Downloader` (0 LOC, `obj/` only) and `tests/Deluno.Downloader.Tests` (no
`.csproj`) deleted. Referenced by nothing; solution builds clean without them.

### Step 1 — split `Deluno.Platform`

Into: `Deluno.Security` (auth, users, API keys) · `Deluno.Connections` (indexers,
download clients, path mappings) · `Deluno.Libraries` (libraries, views, routing,
destination rules) · `Deluno.Quality` (profiles, quality model, custom formats) ·
`Deluno.Intake` (sources, exclusions, previews) · `Deluno.Notifications`.
`Deluno.Platform` keeps only what is genuinely shared plumbing.

Do it one context at a time, moving files and the matching slice of
`SqlitePlatformSettingsRepository`. **Test after each**, not at the end.

### Step 2 — one media engine

**Precondition:** a `Deluno.Series.Tests` suite exists and covers the behaviour
that differs from Movies. Merging two engines with only one of them tested is
how a silent behavioural regression gets in.

Extract the fourteen shared methods into `Deluno.Media` with a
`MediaKind`-parameterised repository over the shared tables, leaving
`Deluno.Movies` and `Deluno.Series` holding only what differs: episodes, seasons
and air dates on one side, release dates and availability on the other.

This is the step that stops the copy-paste, and it is also the riskiest — do it
after Step 1, when the dependency graph is clean enough to see what breaks.

### Step 3 — `Deluno.Recovery`

Give the cleanuparr/huntarr surface an actual module: import recovery, stalled
and blocked releases, orphaned files, seeding retention, safe removal. Everything
that today is scattered across nine projects, with one activity story.

### Step 4 — split the worker

One handler per job type, registered by type rather than switched on, and one
lane runner that takes a handler set. The 2,029-line file becomes a thin loop
plus a folder of small handlers.

## Consequences

**Good.** The dependency graph stops lying — a metrics endpoint can live where it
belongs. Adding a method to the media engine stops meaning writing it twice.
Recovery becomes reviewable as one thing. Job handlers become unit-testable
without a worker.

**Cost.** Steps 1 and 2 touch nearly every file in the C# tree. Anything in
flight will conflict. This must happen on a quiet branch, not alongside feature
work.

**Risk.** Step 2 is where a subtle behavioural difference between the two engines
could be flattened by accident. The 235 persistence tests are the safety net and
must stay green at every commit; where they do not cover a difference, add the
test *before* merging the code.

## Not doing

- Rewriting the storage layer. SQLite-per-context is working and is not the problem.
- Introducing a mediator/CQRS layer. The problem is where code lives, not how it is called.
- Touching the web app. It was restructured this session and is not implicated.
