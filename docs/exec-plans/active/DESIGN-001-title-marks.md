# DESIGN-001 — The mark on a title

Settled 2026-08-27 with James, over a long design pass. This is the decided
design for [#300](https://github.com/jampat000/Deluno/issues/300) and
[#302](https://github.com/jampat000/Deluno/issues/302), and the vocabulary
[#301](https://github.com/jampat000/Deluno/issues/301) (Subber) inherits.

Rendered reference, every mark drawn at real poster sizes:
<https://claude.ai/code/artifact/f6e8656a-8e43-4b5b-a4a2-215e983e8232>

## The idea

A title gets **one dot** and **one bar**.

- **The dot is the title itself** — where it has got to on a four-rung ladder.
- **The bar is what you asked for beyond the title** — subtitle languages on a
  movie, episodes on a show.
- **A half-grey dot means you are not monitoring it.**

Nothing else appears on a title. Failures, machinery health and anything
genuinely blocked on a person live in Transfers, Activity and Needs You — which
is what frees red for *Missing* and keeps amber meaningful.

## The ladder

| Mark | Name | Means | Can be half? |
|---|---|---|---|
| 🔴 red | **Missing** | It is out and you do not have it. | yes |
| 🔵 blue | **Downloading** | Coming down, processing, or importing. | no |
| 🟢 green | **Upgradable** | Here and watchable tonight, with room to get better. | yes |
| 🟡 gold | **Quality met** | The quality your profile asked for. Deluno has stopped looking. | no |
| ⬜ slate | **Upcoming** | Not released, or the episode has not aired. | yes |

Missing → Downloading → Upgradable → Quality met is the order a title climbs.
Nobody has to be taught that gold is above green.

### Why these names

- **Upgradable** beat On disk, Ready, Done, Complete, Good copy, Watchable and
  Upgrade needed. It states a fact rather than describing storage ("on disk"),
  passing a verdict ("watchable" is faint praise) or nagging ("upgrade needed").
  It is also the only one whose count is worth reading: *3 upgradable* is a
  to-do list, *3 watchable* is not.
- **Quality met** beat Best copy and Best version, which over-claim — it is not
  the best copy in existence, it is the one your profile asked for.
- **Upcoming** beat Unreleased: an episode airs rather than releases, and one
  word has to cover both.

## The colours

Chosen for what each state already means, not for hue spacing.

| Token | Light | Dark | Why |
|---|---|---|---|
| Missing | `hsl(0 84% 48%)` | `hsl(0 84% 62%)` | Red is absence, and absence is what a library is for filling. |
| Downloading | `hsl(207 92% 45%)` | `hsl(207 96% 62%)` | Blue is the in-progress colour of every progress bar ever made. |
| Upgradable | `hsl(145 72% 34%)` | `hsl(145 78% 52%)` | Green is *you have it*. |
| Quality met | `hsl(42 96% 40%)` | `hsl(44 98% 58%)` | Gold is a rung above green without being taught. |
| Upcoming | `hsl(265 62% 52%)` | `hsl(265 82% 72%)` | Violet — the one hue left, and what a calendar uses for something scheduled. **Revised 2026-08-27**, see below. |
| Idle half | `hsl(220 10% 82%)` | `hsl(217 16% 26%)` | One grey, shared by every half. |

Red, blue and green already exist in `apps/web/src/index.css`. **Gold and the
Upcoming violet are the only new tokens.**

### Upcoming was a slate, and the slate did not survive contact

It was muted on purpose — "nothing is happening and nothing should be". On a
real shelf that put it a few percent of lightness away from the idle grey, so a
**halved** Upcoming dot was grey beside grey and read as *unmonitored* rather
than as *not out yet*. The two things it most needed to be told apart from were
the two it looked most like.

Violet is the only slot left. Red is Missing, blue is Downloading and also the
app's primary, green is Upgradable, gold is Quality met, and amber is reserved
for "a person is needed" and never appears on a title. Violet also passes no
verdict of its own, which suits a rung that means "nothing to do yet".

## The half

`linear-gradient(90deg, <state> 0 50%, <idle> 50% 100%)`.

**It means the monitoring toggle and nothing else.** A library with no indexer,
or automation on pause, also stops things happening — but those belong to the
library, not smeared across every poster in it, and they already have a home in
Needs You.

It appears only where monitoring is deciding something *now*:

- **Missing** half — nothing will go looking for it.
- **Upgradable** half — it stays the copy it is.
- **Upcoming** half — it will pass you by on release.
- **Downloading** never half — a transfer underway finishes regardless.
- **Quality met** never half — it has left the lifecycle. If the file later
  disappears the title drops back to Missing and the question returns with it.

Rejected alternatives: a **ring** around the dot (bled at 13px), and
**desaturation** (put three drained dots together and they are the same grey —
it removes the very channel that told them apart).

## The bar

`linear-gradient(to right, green 0 <have>%, red <have>% 100%)`, sitting on the
bottom edge of the poster.

**It is subtitle languages, on both media.** *(Revised 2026-08-27 with James.)*

- **Movies: one file.** Two languages asked for and you have English is half
  green.
- **Shows: the same sum, over the episodes you hold.** Thirteen episodes with
  the same two languages asked for of each is 26 slots; four episodes short a
  language makes it 22/26 green.
- **No languages asked for: no bar.** *(Revised 2026-08-27.)* It used to draw a
  grey one, to keep "the shelf's shape" so nothing was relaid out when the
  numbers started arriving. That reason does not survive reading the CSS — the
  bar is `absolute … bottom-0`, painted over the poster, and takes no layout
  space, so adding or removing it re-lays out nothing. The grey stripe bought a
  benefit that never existed and paid for it with a mark on every poster in the
  library that said nothing at all.

**Counted only over the files a title actually has.** Counting the episodes you
are missing would drag the bar down for a reason that has nothing to do with
subtitles — and the dot at the top of the same poster already says the show is
Missing.

**Subtitles never change the dot.** A movie short of a language is still Quality
met, because the title is exactly what you asked for. The bar measures the
extras; the dot is the title. Red on the bar means *that extra is missing*; red
on the dot means *the title is missing*. Same word, different subject, and the
subject is what the mark is drawn on.

### Why it stopped being episodes

It used to be subtitle languages on a movie and **aired episodes** on a show, so
the identical strip of pixels answered a different question depending on which
shelf you were standing in front of — and a show could never show its subtitle
state at all, because its bar was already spent.

Rejected on the way: **folding subtitles into the episode count**, so an episode
only counts as held when it has its languages too. One bar, same meaning as a
movie's — but a show with all 36 episodes and no subtitles would draw an empty
bar, which reads as *no episodes*. And **a second bar on shows only**, which
makes TV posters carry something Movies posters do not, which is the whole
defect again.

**Episode counts are off the poster.** They are on the show's own page, where
the season list already lives, and the show's dot still carries its rung.

## Shows

Same five marks. **The dot takes the lowest rung any aired episode is on** —
missing, then downloading, then upgradable, then quality met — so it never
overstates how well a show is doing.

The episode counts themselves are not on the poster. They decide the dot, and
`airedWithFileCount` also says how many files the bar is measured over, but the
numbers are read on the show's own page.

## The counts and filters

The toolbar changes with it. Today's *"1 downloaded · 10 missing"* becomes:

> **11** movies · **1** quality met · **0** upgradable · **0** downloading · **10** missing · **0** upcoming

**"Downloaded" goes** — a movie below target is downloaded too, so the word could
never tell you which.

**Every chip is colour-coded, and the colour is on the number.** *(Revised
2026-08-27.)* It was a 6px dot to the left of the label, which is too small to
work as a legend for a wall of posters, and three of the seven chips had no
colour at all. The count is the part you read, so the count wears the mark.

**Monitored and Unmonitored left the row entirely.** *(Revised 2026-08-27.)*
They were the two chips that could not be given a colour, and the reason is that
**monitoring is not a state** — it is whether Deluno acts on one, and it
multiplies across all four rungs. Any of Missing, Upgradable, Quality met or
Upcoming can be monitored or not.

Keeping them in a row of states had a second cost, worse than the visual one:
the two were *mutually exclusive*, because a title could only carry one filter
value. **"Missing, and I have told Deluno to leave it alone" could not be asked
for.** Monitoring is its own control in the toolbar now and its own axis on the
query, so the row picks a state, the control picks an intent, and both narrow
together. On a poster it stays what it always was — the *half* on the dot, which
is the same idea: a modifier on whatever colour is already there.

The state counts on the chips are taken inside the monitoring scope, and the
monitoring counts inside the state scope, so each control says what choosing it
would actually give you.

## What this means for the stored vocabulary (#300)

Four stored values, one meaning each, replacing the three that shared a word:

| Stored | Mark |
|---|---|
| `missing` | Missing |
| `upgrade` | Upgradable |
| `covered` | Quality met — **this is what `waiting` wrongly means today** |
| `upcoming` | Upcoming — **new, and actually set**, from release and air dates |

**Downloading is not a stored wanted status.** It is live transfer state, and
deriving it from a wanted status is exactly the bug #299 fixed. It has to come
from download telemetry.

## Before any of this can be drawn — the blocker (**cleared 2026-08-27**)

**The library grid did not receive the data this design needs.**

- `MovieListItem` and `SeriesListItem` carried `monitored`, `hasFile` and
  `currentQuality` — **no wanted status, no cutoff flag, no release dates, no
  episode counts** (`apps/web/src/lib/api/types/catalogue.ts`).
- `ListPageAsync` in `SqliteMovieCatalogRepository` selected `FROM movie_entries m`
  with **no join to `movie_wanted_state`**.
- The wanted status the grid did use came from `/api/movies/wanted`, whose
  `recentItems` is **`LIMIT 25`**. So at most 25 titles in a library had a
  wanted status on the grid; every other card fell back to `hasFile`.

That last one is why it looked fine on the lab rig — 11 movies, all inside the
25 — and would have degraded silently at the 20,000-item invariant.

### What landed

Both catalogue pages now carry their own search state, from a `LEFT JOIN` that
binds **one** wanted-state row per title (`CatalogueWantedState`, shared by both
repositories). `wantedStatus`, `wantedReason`, `libraryId`, `targetQuality`,
`qualityCutoffMet`, `lastSearchUtc` and `nextEligibleSearchUtc` ride the page;
the movie payload already carried the release dates and now exposes them to the
web contract. Series pages additionally carry `episodeCount`,
`airedEpisodeCount`, `airedWithFileCount`, `airedUpgradableCount` and
`nextAirDateUtc`, from one grouped pass over the page's own shows.

`/api/movies/wanted` is no longer fetched by the grid at all.

Three things worth carrying forward:

- **The same join replaced eight correlated subqueries per row**, which could
  not keep their own answers together: each took the first row with a non-null
  value for *its* column, so a title in two libraries could report one library's
  quality beside another's file path. One row now answers for the title, and it
  is deliberately the row the Downloaded and Upgrades filters select on, so the
  card and the filter that produced it agree.
- **The page is still a seek.** A grouped subquery or an aggregate join would
  have materialised every wanted row before returning fifty.
  `The_page_reaches_the_wanted_state_by_key_and_never_scans_it` asserts the query
  plan, because nothing about a wrong plan looks wrong until the twenty-thousandth
  title.
- **The same defect lived next door, on the detail pages.** `movie-detail-page`
  and `show-detail-page` searched that same 25-item summary for the one title
  they were already showing — so opening the 26th-most-recently-touched title
  lost its library, its target quality and its cutoff, and left a Defer button
  that could only 404. Fixed the same way: `GetByIdAsync` carries the state, on
  both the shared-media-state path and the repositories' own.

## Build order

1. ~~**Data.** Extend the paged catalogue query and contracts as above. Test at
   scale, not on eleven movies.~~ **Done** — `CatalogueSearchStateOnPageTests`,
   including a 2,000-title walk and a query-plan guard.
2. **#300's split.** Rename `waiting` → `covered`, add `upcoming` set from
   release and air dates, teach the episode paths the same words, migrate.
   Pin with the test the issue asks for.

   Two things the data work turned up for this step:
   - **`episode_wanted_state` already writes `covered` and `missing`** while
     `movie_wanted_state` writes `waiting`, `upgrade` and `missing`. The episode
     vocabulary is already half-way to the target; the movie one is not.
   - **`NormalizeWantedStatus` coerces anything it does not recognise to
     `missing`.** A typo, or a new value written before the reader learns it,
     becomes "go and download this" rather than an error. Worth making loud as
     part of the rename, since the rename is exactly when a value gets written
     that an old reader does not know.
   - **The show detail page overstates what is missing.** Slow Horses reads
     "Find 36 missing episodes" and `MISSING 36` when only 30 have aired — the
     same mistake the bar is forbidden to make. The counts to fix it now exist
     on the payload.
3. ~~**One table.** State → colour → label, in one module, with a test that no
   screen hard-codes a tone.~~ **Done** — `lib/status-tones.ts` and
   `status-tones.test.ts`. Four tone vocabularies merged, not two.
4. ~~**The mark.** The dot and bar components, then the grid, list, toolbar and
   detail page read the table.~~ **Done.** The dot, the half and the bar are in
   `components/ui/title-mark.tsx`, and the grid, the list, the toolbar, both
   detail pages, the dashboard shelf, the calendar and the two episode lists all
   read them.

   The first pass finished the grid and the toolbar and stopped there, and the
   guard test did not notice, because it watched for one spelling of the defect —
   `tone="x"` beside a state's label — and **four more tables were spelling it
   other ways**:

   - `MEDIA_STATUS_PRESENTATION`, a `bg-*`/`variant` map over an eleven-value
     `MediaStatus`. A `MediaItem` could only ever hold two of those values,
     `downloaded` and `missing`, both from `hasFile` alone; the other nine
     described a transfer and nothing ever set them on a title. It painted the
     missing case **amber** — the one signal reserved for "a person is needed".
   - `WANTED_STATUS_PRESENTATION`, a *second* colouring of the four stored wanted
     statuses: Missing blue there and red on the poster, Quality met green there
     and gold on the poster.
   - `quickFilterConfig`, which wrote the mark colours out by hand three lines
     under a comment calling that row the legend.
   - The two detail-page headers, each picking a Badge `variant` per status —
     amber for Missing and Upgradable.

   And a fifth that was pure dead weight: `filterAndSortLibraryItems` and its
   45-value `FilterField` union, imported by nothing since the catalogue became
   server-paged. Its `downloading` and `needsAttention` branches tested values
   nothing ever set, so both could only match nothing; its `missing` branch meant
   "no file"; and `isUpgradeCandidate` re-derived Upgradable from a
   quality-string comparison rather than the stored status. Four definitions of
   Upgradable, one of them unreachable.

   All five are gone. `MediaItem` has no `status` at all now — what a title is
   doing comes from `wantedStatus` and the episode counts, through `titleMark()`.
   The guard is restated in the shape the offenders actually took: outside
   `status-tones.ts`, no module may put a colour on the same line as a mark's
   name, whether it spells the colour `tone`, `variant` or `bg-*`.

   **Three restatements went with them**, all found by looking at the running
   app rather than by any test. A movie's summary strip said **FILE: Missing** in
   amber beside **CUTOFF: Below target** in amber — the mark, twice, in the one
   colour it is never allowed to wear, and "below target" claimed a comparison
   against a file that did not exist. It is three cells now: quality, monitoring,
   import issues, none of them the mark. Every episode row carried a **File**
   column saying "Not imported" next to a **Status** column saying "Missing";
   the column is gone. And the show strip spent amber on its Missing and
   Upgrades counts, which are work Deluno is already doing — they wear the marks'
   own red and green.

   **And the dashboard, which had survived both passes** because a screen that
   invents a *new name* for a state has no mark label on the line for a colour
   rule to notice. Its opening strip counted "Watching for", "Still missing" —
   in amber — and "Could be upgraded"; the ring beside it drew "On disk", "Still
   missing" and **"Upgradeable"**, one letter off the mark it was drawing, which
   is how you can tell it was written from memory rather than read from the
   table. Both read the table now, and the strip is the design's own counts line:
   *In your library · Quality met · Upgradable · Missing*. The ring also stopped
   subtracting its way to a bucket — `coveredCount` was on the payload the whole
   time. A second guard covers this shape: the names DESIGN-001 retired may not
   come back.

   Needs You went with them. It carried "16 titles are still missing" and "N
   retry windows pending", both amber, on an install whose sidebar was
   simultaneously saying *All good · Nothing needs you*. Neither needs a person —
   Deluno searches on its schedule, and a retry window says in its own text that
   it will try again. A badge that lights up when nothing is wrong is how people
   learn to stop looking at it.

   Two smaller things fell out of it too. The list row said monitoring three
   times over — a halved dot, the word in the meta line, and the status cell — so it
   says it once, in the Status column. And the calendar was the last place still
   asking "is there a file for this anywhere" as its own correlated subquery,
   which is why it had to invent *"Watching for it"* in blue for a title the
   shelf beside it was calling Missing in red; `MovieCalendarItem` carries the
   wanted status now, through the same one-row join every other page uses.
5. **Live transfer state** for Downloading, wired from telemetry rather than
   inferred. `titleMark()` already takes an `isTransferring` flag; nothing sets
   it, and there is deliberately no Downloading chip in the filter row until
   something does.
6. **Subber (#301)** inherits the vocabulary and needs no new words. The bar's
   landing site exists: `SubtitleLanguagesWanted`/`Held` on both catalogue
   contracts, zero, and a title with no languages asked for draws no bar.

## Related, and settled while finishing step 4

**#303 — automatic per-episode search.** Three pieces that each worked and never
met: the `episode.search` job type, `EpisodeSearchJobHandler`, and
`PlanEpisodeSearchesAsync`, which nothing called. A show missing four scattered
episodes was searched for as a *show*, found nothing at the series level, and the
four were never asked for.

The planning was expected to go in the heartbeat's automation lane, beside
`PlanLibrarySearchesAsync`. It went **inside the library search cycle** instead —
at the end of `LibrarySearchJobHandler.SearchSeriesAsync`. The lane would have
needed its own copy of every gate the cycle has already passed: the time-of-day
window, the search interval, missing-versus-upgrade, the manual override and
`MaxItemsPerRun`. A second copy of a scheduling rule is how the last four defects
in this codebase were built. Riding on the cycle, the episode pass is due exactly
when the series pass is due and asks for the same half of the work.

`ListEligibleWantedEpisodesAsync` grew `ignoreRetryWindow` and `wantedStatus` to
mirror `ListEligibleWantedAsync` exactly — without them a manual "search now"
would have skipped the episode half, and an upgrade-only cycle would have gone
looking for missing episodes anyway.

## Still open for James

- **The half, now that medium and large posters carry the word.** The mark says
  "MISSING" in text, the meta line below says "Monitored", and the dot is halved
  — three statements, two of them about monitoring. On a small poster the half is
  the only way to see it, so it earns its place there. Worth deciding whether it
  should stay on the sizes that have room for words.
- **Gold against green on a real shelf**, which the rig can now show: one movie is
  Quality met among ten Missing.

## Left for James's eyes

Two things no argument settles, both visible on the rendered reference:

- **Gold reading as a rung above green** on a real shelf. If it does not feel
  like a step up, it is the wrong gold, not the wrong idea.
- ~~**Upcoming's half against the idle grey.**~~ **Answered 2026-08-27:** it did
  read as one flat dot, and as unmonitored rather than not-out-yet. Upcoming is
  violet now.
