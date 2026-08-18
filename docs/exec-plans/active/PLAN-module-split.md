# Execution plan — module split

Companion to `ADR-001-module-boundaries.md`, which holds the measured evidence
and the decision. This file is the runbook: what to do, in what order, and how to
know each step worked.

**Branch to start from:** `ux/list-drawer-media-plans` @ `98a1c70`
(50 commits ahead of `main`, pushed, everything green).

**Do this on its own branch.** Steps 1 and 2 touch nearly every C# file; anything
in flight will conflict.

---

## Ground rules

1. **One context per commit.** Never move two at once. A failed build in the
   middle of a six-context move is unrecoverable without `git reset`.
2. **Green after every commit.** The full gate is below. If a step cannot be made
   green, revert it rather than stacking the next one on top.
3. **Move, then change — never both.** A commit that relocates a file must not
   also edit its logic. Reviewers cannot see a behaviour change inside a 4,000-line
   diff of moved lines.
4. **No regex over C#.** Same rule the web app learned the hard way: nesting
   varies too much. Move whole files; edit call sites by hand.

### The gate

```bash
dotnet build Deluno.slnx
dotnet test tests/Deluno.Persistence.Tests/Deluno.Persistence.Tests.csproj
dotnet test tests/Deluno.Platform.Tests/Deluno.Platform.Tests.csproj
dotnet test tests/Deluno.Movies.Tests/Deluno.Movies.Tests.csproj
dotnet test tests/Deluno.Integrations.Tests/Deluno.Integrations.Tests.csproj
```

Baseline to preserve: **Persistence 235, Platform 86, Movies 18, Integrations 52,
Tray 3.** Any drop is a regression, not a "test that needed updating".

The API must be stopped before a build or the DLL copy fails on a file lock:

```powershell
Get-Process -Name "Deluno.Host" -ErrorAction SilentlyContinue | Stop-Process -Force
```

---

## Step 0 — dead scaffolding ✅ done

`Deluno.Downloader` and `tests/Deluno.Downloader.Tests` deleted at `98a1c70`.

---

## Step 1 — split `Deluno.Platform` (16,017 LOC → six contexts)

The target split, and what moves into each:

| New project | Takes |
|---|---|
| `Deluno.Security` | auth, users, API keys, `UserAuthorization` |
| `Deluno.Connections` | indexers, download clients, path mappings |
| `Deluno.Libraries` | libraries, library views, routing, destination rules |
| `Deluno.Quality` | quality profiles, quality model, custom formats, presets |
| `Deluno.Intake` | intake sources, list exclusions, previews |
| `Deluno.Notifications` | notifications and webhooks |

`Deluno.Platform` keeps only genuinely shared plumbing (settings snapshot,
processing, migration) — and should end well under 4,000 LOC.

### Order

Do **Security first**. It is the most self-contained and every other context
depends on `UserAuthorization`, so getting it out first makes the rest cleaner.
Then `Notifications` (small, few callers), then `Intake`, `Quality`,
`Connections`, `Libraries` (largest, most entangled — last, when the pattern is
established).

### Per-context recipe

1. `dotnet new classlib -o src/Deluno.<Name>`, add to `Deluno.slnx`, reference
   `Deluno.Infrastructure` + `Deluno.Contracts`.
2. Move the `Contracts/*.cs` for that context.
3. Move its slice out of `SqlitePlatformSettingsRepository.cs` (5,519 LOC) into a
   new `Sqlite<Name>Repository.cs`, plus the matching interface.
4. Move its endpoint group out of `PlatformEndpointRouteBuilderExtensions.cs`
   (4,033 LOC) into `<Name>EndpointRouteBuilderExtensions.cs`.
5. Register in `PlatformServiceCollectionExtensions` → move to
   `<Name>ServiceCollectionExtensions`; wire in `Deluno.Host`.
6. Fix call sites. Run the gate. Commit.

### Watch for

- **Migrations stay put.** The Platform SQLite database is one file; splitting
  the C# does not split the schema. `V0001`–`V0018` remain in
  `Deluno.Platform/Migrations` unless and until the databases themselves split.
  `MigrationRunnerTests` asserts the Platform count is **18** — it must stay 18.
- **`UserAuthorization.RequireAuthenticatedAsync`** is called from nearly every
  endpoint in the app. When it moves to `Deluno.Security`, every endpoint file
  gains a using. Expect a wide but mechanical diff.
- **`IPlatformSettingsRepository` is injected almost everywhere**, often *only*
  for the auth check. Once Security owns auth, most of those injections can go —
  but do that as a separate commit after the move, not during it.

### Done when

`Deluno.Platform` no longer contains indexers, download clients, libraries,
quality, intake, notifications or security, and the gate is green.

---

## Step 2 — one media engine

**Precondition — do not start without this.** `tests/Deluno.Series.Tests` exists
and covers: episode catalogue sync (upsert without clobbering `has_file`,
`monitored`, `file_path`, `imported_utc`), season grouping, episode wanted-state
backfill, and the wanted/eligible queries. `Deluno.Series` is 6,640 LOC with no
test project today; merging it into a shared engine untested is how a silent
regression lands.

### The fourteen duplicated methods

```
ListAsync                      GetByIdAsync
AddAsync                       ImportExistingAsync
EnsureWantedStateAsync         DeferWantedSearchAsync
SkipNextWantedSearchAsync      ListEligibleWantedAsync
GetWantedSummaryAsync          GetImportRecoverySummaryAsync
UpdateMetadataAsync            ListSearchHistoryAsync
ListTrackedFilesAsync          GetDailyMetricsAsync
```

### Approach

Create `Deluno.Media` with a `MediaKind` (`Movies` | `Tv`) parameterised
repository covering the shared tables. `Deluno.Movies` and `Deluno.Series` keep
only what genuinely differs:

- **Series:** seasons, episodes, air dates, catalogue sync, episode wanted-state
- **Movies:** release dates, minimum availability, replacement protection

Move one method at a time. After each, both engines must still pass their suites.

### Watch for

- The two engines' SQL is *similar*, not identical — table and column names
  differ (`movie_entries` / `series_entries`, `movie_id` / `series_id`). The
  shared implementation needs a table-name map, not string interpolation of
  user input.
- `ImportExistingAsync` differs meaningfully: the series version also writes
  episodes. That difference is real and must survive.

---

## Step 3 — `Deluno.Recovery`

Cleanup concerns currently span **9 of 12 projects**. Give them one module:
import recovery, stalled and blocked releases, orphaned files, seeding
retention, safe removal.

Start by listing every call site (`grep -rn "cleanup\|recovery\|orphan\|stalled\|retention" src --include=*.cs`)
and deciding for each whether it is *policy* (moves to Recovery) or *mechanism*
(stays where the I/O is). Only then move code.

---

## Step 4 — split the worker

`DelunoHeartbeatWorker.cs` is 2,029 LOC in a 4-file project, dispatching every
job type through one switch.

Target: an `IJobHandler` with `JobType` and `HandleAsync`, one small class per
type, registered in DI and resolved by type. The lane runner keeps the timer,
the settings gate and the scope, and takes a handler set.

**Preserve:** the `AutoStartJobs` gate (`settings.AutoStartJobs` — the worker
skips every tick when false; this is deliberate, see the handover doc) and the
three existing lanes: `search`, `import`, `maintenance`.

Each handler becomes unit-testable without a worker, which is the point.

---

## Not doing

- Rewriting storage. SQLite-per-context works and is not the problem.
- A mediator/CQRS layer. The problem is where code lives, not how it is called.
- Touching the web app. It was restructured this session and is not implicated.
- Splitting the SQLite databases. That is a separate decision with a migration
  cost; the C# split does not require it.
