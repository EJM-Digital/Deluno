# AUDIT-002 — Status vocabularies and colours

Raised 2026-08-27, out of [#300](https://github.com/jampat000/Deluno/issues/300). Two halves: the
**colours**, parked for a wider discussion because the fix crosses every screen
that shows a state, and the **vocabularies** — the words themselves — where the
sweep found live defects. Those are listed at the bottom and are being fixed as
they are confirmed.

## The rule, which already exists

[#290](https://github.com/jampat000/Deluno/issues/290) settled it when it took hue away from navigation:

| Hue | Means |
|---|---|
| **green** — `success` / `ok` | Healthy. Done. *"A colour whose absence you scan for."* |
| **amber** — `warning` / `warn` | Needs **you**. Nothing happens until you act. |
| **blue** — `info` | Work in motion. #290 names the cases: *job running or queued, transfer moving, processing*. |
| **red** — `destructive` / `bad` | Failed. |
| **grey** — `muted` / `idle` | Nothing to do. Off, unknown, not applicable. |

Nothing written since has been checked against it, because nothing in the
codebase states it. Each screen decides its own tone at the point of use.

## One state, more than one colour

The same thing, at the same moment, in different parts of the app.

| State | Transfers list | Pipeline strip | Library card | Should be |
|---|---|---|---|---|
| A release ready to import | **green** — `queue-screen.tsx:1047` | **grey** — `acquisition-pipeline.tsx:83` | **blue** — `media-status-presentation.ts` `importReady` | blue: mid-pipeline, not finished |
| Monitored title with no file | **blue** "Watching for it" — `calendar-page.tsx:430` | — | **amber** "Missing" — `media-status-presentation.ts` | blue: Deluno is looking, nothing needs you |
| Held for the processor | **blue** — `queue-screen.tsx:1048` | **blue** — `acquisition-pipeline.tsx:81` | **amber** "Waiting for processor" — `media-status-presentation.ts` | blue: two of three already agree |

## Amber where nothing needs you

Amber is the one signal that has to stay trustworthy. Four places spend it on
work that is proceeding normally, which teaches people to stop reading it.

| Chip | Where | Why it is wrong |
|---|---|---|
| Running | `job-status-constants.ts:60` | A job in flight. #290 names this blue by name. |
| Rate-limited | `connections/format.ts:6` | Deluno backed off on purpose and resumes itself. |
| Missing | `media-status-presentation.ts` | It is out, Deluno is looking. You do nothing. |
| Waiting for processor | `media-status-presentation.ts` | In flight, and blue everywhere else. |

## Three smaller ones, same cause

| Chip | Where | Why it is wrong |
|---|---|---|
| Importing — green | `acquisition-pipeline.tsx:84` | Green is *done*. This is in motion. |
| Queued — grey | `job-status-constants.ts:57`, `acquisition-pipeline.tsx:74` | Grey is *nothing to do*. #290 names queued blue. |
| Category route — blue | `connections-screen.tsx:582` | A configuration fact, not motion. |

## And two vocabularies for the same five ideas

`Chip` takes `ok warn info muted bad`. `StatusLed` takes `ok warn info idle danger`.
Same five colours, two sets of words, so neither can be checked against the other
and nothing can assert that a state is coloured the same way in both.

## Proposal

One table mapping **state → tone → label**, the way `lib/configuration-areas.ts`
now holds the area explainers, with a test that pins it. Merge the two tone
vocabularies onto one set of names. Then a state cannot be coloured twice,
because there is only one place that colours it.

## Why this blocks part of #300

#300 splits `wanted_status` into four values, and the tone for each is the
question. Under the rule above the answers are not a matter of taste:

- `covered` → green. It is done.
- `upgrade` → blue. In motion.
- `missing` → blue, not amber. Deluno is looking; nothing needs you.
- not-out-yet → grey. Nothing to do.

The label for the fourth value is still open: *Not out yet* / *Unreleased* /
*Waiting*. "Waiting" matches the stored word but collides with the processor
hand-off status, which uses the same word for the opposite meaning.

A title inside its retry window stays `missing` under this scheme: Deluno is
working on it and the window is only pacing — the default is 24 hours per
library, set by `RetryDelayHours` and stamped after every attempt in
`LibrarySearchJobHandler.cs:204`. Grey would say nothing is happening, which
is false.

---

# Part two — the vocabularies

Twenty status columns across 58 tables, swept from the schema up rather than
from memory. The colours above are a presentation problem. These are not.

## Fixed — `download_dispatches.import_status` (`f56e0a9`)

Written as `imported`, read as `completed` in three places, so all three matched
nothing for the life of the column.

- **The archive sweep never selected a row**, so no dispatch has ever been
  archived, and every active query filters `status != 'archived'` — meaning
  every successfully imported dispatch stayed in the working set the Transfers
  list, the metrics, the routing statistics and the ranking training data all
  read. Against the 20,000-item invariant, this is the one that mattered.
- **`successful_imports` served zero** for the life of the endpoint.
- The poller's realtime success backstop could only ever take its failure
  branch.

Two call sites had already hit it and accepted either word locally without
chasing it to the writer. Fixed by normalising on write, pointing the readers at
the one word, and a migration for any row holding the other.

## Open — `wanted_status` has two words for one state, and one word nothing writes

- **`covered`** is written as raw SQL for episodes in two places
  (`SqliteSeriesCatalogRepository.cs:1864` and the catalogue-sync backfill),
  bypassing `NormalizeWantedStatus`, which maps anything it does not recognise to
  `missing`. Harmless only because the episode path never calls that normaliser.
  Movies and shows call the same state `waiting`. One state, two words, split by
  which entity you are looking at.
- **`'wanted'`** is read by `ListEligibleWantedEpisodesAsync`
  (`SqliteSeriesCatalogRepository.cs:2668`) and written by nothing. Its test
  seeds the value by hand, with a comment explaining that the query needs it.

Both fold into [#300](https://github.com/jampat000/Deluno/issues/300).

## Open — automatic per-episode search does not exist

Found while chasing `'wanted'`. The whole path is orphaned, in three pieces that
are each individually plausible:

- `ListEligibleWantedEpisodesAsync` — the eligibility query. No production
  caller, and it filters on a value nothing writes.
- `PlanEpisodeSearchesAsync` (`SqliteJobStore.cs:1869`) — the only code that
  creates an `episode.search` job. No callers.
- `EpisodeSearchJobHandler` — handles a job type nothing queues.

`LibrarySearchJobHandler` searches series at the **title** level
(`ListEligibleWantedAsync` over `series_wanted_state`), never per episode. Manual
episode search works, through a synchronous endpoint. So Deluno cannot
automatically search for one missing episode, which Sonarr does — a gap against
[#194](https://github.com/jampat000/Deluno/issues/194) rather than a bug, and
worth its own issue.

## Noted, not defects

- **`library_automation_state.status`** holds `attention`, `idle`, `queued`,
  `ready`, `requested`, `running`. The UI shows none of those six words — it
  derives *Searching / Scheduled / Paused / Not configured* from other fields.
  Two vocabularies, but they never meet.
- **`health_status`** once held `ready` / `paused` / `attention`; V0003 migrated
  them to `untested` / `disabled` / `degraded`. Handled history. The frontend's
  `healthChip` has no case for `disabled`, but reaches it only through the
  `isEnabled` check first, which returns *Off*.
- **The custom filter engine is dead.** `filterAndSortLibraryItems`,
  `matchesCustomRule`, `parseCustomRules`, the comparator helpers and
  `isUpgradeCandidate` have no consumer. A status bug lived in there undetected
  because no screen could show it (`f505cb2`).
