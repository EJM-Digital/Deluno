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
  film, episodes on a show.
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
| Upcoming | `hsl(205 16% 58%)` | `hsl(205 18% 60%)` | Muted, because nothing is happening and nothing should be. |
| Idle half | `hsl(220 10% 82%)` | `hsl(217 16% 26%)` | One grey, shared by every half. |

Red, blue and green already exist in `apps/web/src/index.css`. **Gold and the
Upcoming slate are the only new tokens.**

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

- **Films: subtitle languages.** Proportioned to what you asked for. Four
  languages and you have English is a quarter green.
- **Shows: aired episodes.** Thirteen of eighteen is 72% green.
- **Asked for nothing:** the bar stays grey and claims nothing.

**The bar counts what has aired, not what will exist** — otherwise every ongoing
show reads permanently unfinished.

**Subtitles never change the dot.** A film short of a language is still Quality
met, because the title is exactly what you asked for. The bar measures the
extras; the dot is the title.

## Shows

Same five marks. **The dot takes the lowest rung any aired episode is on** —
missing, then downloading, then upgradable, then quality met — so it never
overstates how well a show is doing. The bar says how many are here.

## The counts and filters

The toolbar changes with it. Today's *"1 downloaded · 10 missing"* becomes:

> **11** movies · **1** quality met · **0** upgradable · **0** downloading · **10** missing · **0** upcoming

**"Downloaded" goes** — a film below target is downloaded too, so the word could
never tell you which. **"Monitored" keeps a filter and loses its colour**; the
half already says it on every poster.

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

That last one is why it looked fine on the lab rig — 11 films, all inside the
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
   scale, not on eleven films.~~ **Done** — `CatalogueSearchStateOnPageTests`,
   including a 2,000-title walk and a query-plan guard.
2. **#300's split.** Rename `waiting` → `covered`, add `upcoming` set from
   release and air dates, teach the episode paths the same words, migrate.
   Pin with the test the issue asks for.

   Two things the data work turned up for this step:
   - **`episode_wanted_state` already writes `covered` and `missing`** while
     `movie_wanted_state` writes `waiting`, `upgrade` and `missing`. The episode
     vocabulary is already half-way to the target; the film one is not.
   - **`NormalizeWantedStatus` coerces anything it does not recognise to
     `missing`.** A typo, or a new value written before the reader learns it,
     becomes "go and download this" rather than an error. Worth making loud as
     part of the rename, since the rename is exactly when a value gets written
     that an old reader does not know.
   - **The show detail page overstates what is missing.** Slow Horses reads
     "Find 36 missing episodes" and `MISSING 36` when only 30 have aired — the
     same mistake the bar is forbidden to make. The counts to fix it now exist
     on the payload.
3. **One table.** State → colour → label, in one module the way
   `lib/configuration-areas.ts` now holds the area explainers, with a test that
   no screen hard-codes a tone. This is #302's fix.
4. **The mark.** The dot and bar components, then the grid, list, toolbar and
   detail page read the table.
5. **Live transfer state** for Downloading, wired from telemetry rather than
   inferred.
6. **Subber (#301)** inherits the vocabulary and needs no new words.

## Left for James's eyes

Two things no argument settles, both visible on the rendered reference:

- **Gold reading as a rung above green** on a real shelf. If it does not feel
  like a step up, it is the wrong gold, not the wrong idea.
- **Upcoming's half against the idle grey.** It is the only muted hue in the
  set, so its half is the tightest pair. If it reads as one flat dot at 62px,
  Upcoming needs a real hue rather than a slate.
