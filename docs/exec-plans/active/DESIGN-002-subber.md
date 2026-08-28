# DESIGN-002 — Subber in Deluno

The plan for [#301](https://github.com/jampat000/Deluno/issues/301): move Subber
out of Weir and into Deluno, "cleaner and better than it was".

Read after [DESIGN-001](DESIGN-001-title-marks.md) — Subber inherits its
vocabulary, and the bar under every poster is already waiting for the numbers
this feature produces.

## What Subber is today

In Weir: **~8,500 lines of Python across 81 files**, plus a seven-tab React
page. It fetches subtitles for movies and TV from eight providers, on a
schedule, with upgrades and backoff.

## The finding that shapes the port

**Roughly half of Subber exists only because Weir is not the arr.**

Weir has no library of its own, so Subber had to build one: talk to Sonarr
and Radarr over HTTP, scan and sync what they hold, keep its own copy, store its
own credentials, run its own job lane, and schedule its own work with its own
windows and intervals.

Deluno **is** the arr. Every one of those is already here, load-bearing, and
tested.

| Weir file | Deluno already has | Verdict |
|---|---|---|
| `subber_arr_client.py` | the catalogue itself | **delete** |
| `subber_library_scan/sync_job_handler.py`, `subber_library_service.py` | `movie_entries` / `series_entries`, the import paths | **delete** |
| `subber_credentials_crypto.py` | Connections and their secret storage | **reuse** |
| `subber_jobs_model/ops/inspection*` | `job_queue`, lanes, Activity, dead-letter | **reuse** |
| `subber_schedule_enqueue.py` (361 lines) | library automation: search window, interval, retry delay, `MaxItemsPerRun`, manual override | **reuse** |
| `subber_settings` sonarr/radarr URLs | — | **delete** |
| `worker_loop.py`, `worker_limits.py` | `DelunoHeartbeatWorker` and its lanes | **delete** |

That is not a saving to be enjoyed later; it is the **whole risk of this
feature**. A second scheduler beside the library one, with its own idea of when
a window is open, is precisely the shape that produced every defect this week —
`NormalizeWantedStatus` written three times, `QuickFilter` declared twice, four
tone tables, monitoring as a status. #303 was closed two commits ago by
*refusing* to add exactly this.

**Subber gets no lane, no scheduler and no library of its own.**

## What genuinely ports

The part Weir had to write because nobody else had:

- **Eight provider clients** — OpenSubtitles.org, OpenSubtitles.com, Podnapisi,
  Gestdown, SubDL, SubSource, Subf2m, Yify. Real integration code, real quirks.
- **`subber_subtitle_search_service.py`** (819 lines) — query building, per
  provider search, picking the best result, unzipping, writing the `.srt`.
- **Upgrade** — replace a subtitle with a better one later.
- **Backoff** — adaptive delay, max attempts, permanent skip. Deluno has the
  same idea for releases (`next_eligible_search_utc`); this should read the same
  way rather than inventing a second vocabulary.
- **Hearing-impaired exclusion**, and where the file lands beside the video.

## What it gives back to the mark

`SubtitleLanguagesWanted` and `SubtitleLanguagesHeld` are already on both
catalogue contracts, already zero, and the bar already knows what to do with
them — see DESIGN-001's bar section, revised today so that a movie and a show
ask the same question.

- **Wanted** is the languages asked for **per file**.
- **Held** is how many are present, summed across the files the title has.
- No languages asked for, **no bar** — which is every title until this ships.

So Subber's first visible act is that the bars start meaning something. Nothing
in the mark has to change to receive it.

## Bazarr parity — what to take, and what to do better

The bar is Bazarr: everything that makes it good, made easier to understand and
configure. Most of what is hard about Bazarr is hard because it is a separate
application. Deluno is not, so a good deal of its difficulty simply does not
arise here.

### Free, because of where Deluno sits

| Bazarr has | In Deluno |
|---|---|
| **Path mappings** — its most common support problem, because Bazarr sees different paths from the arr | **Cannot exist.** Deluno owns the file it imported. There is nothing to map. |
| Wanted list | The bar, the Subtitles column and a filter |
| History | Activity |
| Provider throttling, health, anti-captcha plumbing | Connections: health, test, rate-limit backoff, "needs you" |
| Its own scheduler and worker | The library automation cycle |
| Notifications | Already there |
| **Embedded subtitle detection** — knowing what is already inside the MKV so it is not fetched twice | **Already read.** `FfprobeMediaProbeService` returns `MediaSubtitleStreamInfo(Index, Codec, Language)` today. It is not yet used for anything; this is what it was for. |
| **Sidecar detection** — the `.srt` sitting beside the video | Read in the same pass, and the bigger half of the two — see "What *held* actually means" below. |

### Ports from Weir

Eight provider clients, the search-and-write service, upgrades, backoff,
hearing-impaired exclusion.

### New, and worth it

- **A language profile with a cutoff**, per library, beside the quality profile.
  Ordered — English, then Spanish — and a cutoff that says *stop here, this is
  good enough*, which is exactly how quality already behaves in Deluno. One idea
  learnt once.
- **A quality gate on the subtitle itself.** Bazarr expresses this as numeric
  score weights, which nobody can reason about. Deluno should say what it means.
  **What that means was read out of Bazarr's own source rather than guessed —
  see "What Bazarr's score actually says" below.**
- **Timing sync.** The single biggest reason anyone touches subtitles by hand.
  Bazarr shells out to `ffsubsync`/`alass`; Deluno already ships ffprobe
  handling, so the same shape applies.
- **Manual search** — see what was found, its score and its source, and pick one
  yourself. On the title's own page, not a separate screen.
- **Blacklist** — "this one is wrong, never fetch it again", which is the only
  honest answer when an automatic pick is bad.
- **Forced and hearing-impaired variants** as first-class, not flags buried in
  provider options.

### What we will not do

- **Forty providers.** Ship the eight that work, each with health and a test,
  each saying plainly what an account buys you. A provider that fails silently
  is worse than one that is absent.
- **Separate movie and series settings for everything.** Bazarr doubles most of
  its settings this way. Deluno has libraries; a movie library and a TV library
  are already separate things.
- **A seven-tab Subtitles app.** Two settings screens — providers under Find &
  Download, languages under Quality & Release — and the rest appears where you
  already look.

  **This clause holds, and it is worth saying exactly what it protects.** What
  it objects to is Bazarr splitting every setting into a movie copy and a series
  copy, and that still stands because Deluno has libraries.

  The settings themselves are real, though, and #321 lists nine more to come:
  sync thresholds, content modification, adaptive searching, translation,
  language equivalence. The owner, on where they should live: *"in staying with the
  theme in media management we should have a separate subtitles top menu that you
  can change all the settings you need ... and then you can select the library
  you want to apply it to."*

  So it is **two tabs on two existing areas**, and the split is by what the thing
  *is*:

  - **Media Management → Subtitles.** Every sibling tab there is an aspect of a
    library — its folder, its naming, its import policy, its final destination,
    its metadata, its tags. Which languages a shelf wants is another one.
  - **Find & Download → Subtitle Providers.** Every sibling tab there is
    something you connect to. A subtitle provider is a source; it just needs no
    download client, because the file arrives in the answer.

  A first attempt made Subtitles a top-level area of its own, which was a
  misreading of "in staying with the theme in media management" — he meant
  *consistent with how that area works*, not *beside it*. Two tabs is smaller,
  and each one is in the place a reader would look for it.

  The half of his note that changed the most is the second: languages left the
  library edit form entirely. They are per library, and the comparison somebody
  actually wants to make is *across* libraries — "English on everything, Japanese
  on anime" was something you could only work out by opening two forms and
  remembering the first. It is one list of every library now, with the settings
  behind a drawer, which is also what lets the nine outstanding settings land as
  more rows rather than another screen.

## What Bazarr's score actually says

The owner, asked to pick between three definitions of "better": *"this is the thing
that we need to look into with bazaar and how it does it properly."* Right — so
this was read from `custom_libs/subliminal_patch/score.py` and
`bazarr/app/config.py` at Bazarr `master`, not from the wiki and not from memory.

### The table it actually ships

| | episode | movie |
|---|---:|---:|
| `hash` | **359** | **179** |
| `series` / `title` | 160 | 60 |
| `year` | 90 | 40 |
| `season` | 30 | — |
| `episode` | 30 | — |
| `source` | 25 | 30 |
| `edition` | — | 30 |
| `release_group` | 20 | 15 |
| `audio_codec` | 1 | 1 |
| `resolution` | 1 | 1 |
| `video_codec` | 1 | 1 |
| `hearing_impaired` | 1 | 1 |
| `streaming_service` | 1 | 1 |
| **maximum** | **360** | **180** |

Defaults: **90%** for episodes, **70%** for films.

### Two things fall straight out of it

**`hash` is always `max − 1`,** and Bazarr *validates* that — if you edit the
weights and the sum stops working out, it throws yours away and uses the
defaults. So the hash rule is not a weight at all; it is structural. When a
provider matched the file's own bytes, that beats every combination of metadata
except a literally perfect one.

**The other ten weights are not a quality model. They are gates with a
tiebreaker tail.** Run the defaults and the thresholds decode to one sentence:

| Episode, threshold 90% | score | | Film, threshold 70% | score |
|---|---:|---|---|---:|
| right show + year | 69% ✗ | | right title + year | 56% ✗ |
|  + right episode | 86% ✗ | | | |
|  + **same source** | **93% ✓** | |  + **same source** | **72% ✓** |
|  + exact release group | 99% ✓ | |  + edition | 89% ✓ |
| | | |  + exact release group | 97% ✓ |

On both media the *only* thing standing between fail and pass at the shipped
default is **`source`** — WEB vs Blu-ray vs HDTV. Getting the right episode is
not enough; getting the exact release group is more than enough. And the bottom
five weights are 1 point each, so `resolution`, `video_codec`, `audio_codec`,
`streaming_service` and `hearing_impaired` can never change a pass into a fail.
They break ties and nothing else — which is also Bazarr agreeing with
DESIGN-001 that hearing-impaired is coverage, not a disqualification.

**So Bazarr's default, in English, is: *a subtitle cut for the same kind of
release as my file.*** That is the honest translation of "90%", and it is a
sentence a person can hold. It is also not one of the three this document
originally proposed, which is the whole reason for reading the source.

### And its upgrade rule is a cutoff

`upgrade_subs` on, `upgrade_frequency` 12 h, `days_to_upgrade_subs` 7 (max 30) —
and the rule underneath: **a subtitle is only re-examined if it was accepted
*below* the threshold.** Score at or above it and Bazarr stops looking at that
file for ever.

Deluno already has that idea and already has words for it. It is
`UpgradeUntilCutoff` on a quality profile: a ladder, and a rung that means *stop
here, this is good enough*. DESIGN-002 said to reuse it — *"exactly how quality
already behaves in Deluno. One idea learnt once"* — and reading Bazarr confirms
the shape rather than inventing it.

### The proposal

Three rungs, in the order a subtitle climbs, and a cutoff that says where to
stop. No numbers on screen.

| Rung | Means | Bazarr's equivalent |
|---|---|---|
| **Any release** | The right film or episode. Readable; the timing may need a nudge. | ~86% episode / ~56% film |
| **Same source** | Cut for the same kind of release — WEB, Blu-ray, HDTV. Timing is right almost every time. | the shipped default |
| **Made for this file** | The provider matched the file itself, or the subtitle names your exact release group. Timing is guaranteed. | `hash`, or `release_group` |

Deluno already has every input this needs: `SubtitleCandidate.ReleaseName` from
the provider, and `MediaFileNameFacts.Parse` plus quality detection for the file.
Nothing new has to be computed.

**A subtitle below the cutoff is still fetched** — the shelf gets covered
tonight — and stays on the upgrade list until something at the cutoff turns up.
That is what the bar's green means, and it is why gold exists: green is *you can
watch it and Deluno is still looking*, gold is *at the cutoff, Deluno has
stopped*. So this decision is also what unblocks the third colour.

**Not adopted from Bazarr:** the 30-day window on upgrades. It exists because
Bazarr re-scores by walking history; Deluno keeps `next_eligible_search_utc` per
title and language already, so an old subtitle costs nothing to leave on the
list — and "we stopped looking after a month" is the kind of silent give-up this
document already refused for backoff.

## What *held* actually means

The owner, on the first draft of this: *"shouldn't the whole premise of porting
Subber over to Deluno be that it knows when subtitles are downloaded and added?
I get that ffprobe can detect subs in downloaded media but that should only be
part of the equation."*

He is right, and the plan said it badly. Embedded detection is one source of
*held*, not the definition of it. Deluno holds a language for a file when any of
three things is true, and only the third needs finding:

| Source | How Deluno learns |
|---|---|
| **Subber fetched it** | It wrote the file. The row is recorded at that moment, with its provider — no scan involved. This is the primary path once Subber runs. |
| **A subtitle file beside the video** — from a previous Bazarr, from the release, dropped there by hand | The folder scan |
| **A track inside the container** | ffprobe, in the same pass |

So the store is the truth and it carries a `source`. Scanning exists only to
learn about the subtitles Deluno did not fetch — which, on the day somebody
first asks a shelf for English, is every one of them.

The sidecar half matters more than the embedded half. A library that has been
through Bazarr is full of `Movie (2008).en.srt`, and reading only the container
would paint every one of those posters red for a subtitle the reader is looking
straight at.

**Three things are deliberately not guessed at:**

- A bare `Movie.srt` with no language in its name is recorded as `und` and
  counts towards nothing. Reading it as the library's first wanted language
  would be right most of the time, and when it was wrong it would stop Deluno
  fetching a language somebody asked for and never say why.

  **Bazarr answers this and Deluno should copy it.** Walking Bazarr
  (DESIGN-005) turned up *"Treat unknown language embedded subtitles track
  as…"* in its Languages settings — a setting, empty by default. It does not
  guess either; it asks once. That is the missing half of this decision: refuse
  to guess, and give the person who does know somewhere to say so. Empty means
  `und` counts for nothing, which is today's behaviour.
- A **forced** track is not coverage. A file whose only English track is forced
  has English for four lines of Elvish. It is stored, and it does not count.
- **Hearing-impaired** is coverage — it is watchable — and it is not counted a
  second time beside a plain track in the same language.

`en`, `eng` and `English` are one language, through
`SubtitleLanguages.Normalize`. The library setting stores the first, ffprobe
emits the second and a subtitle file is named any of the three; three
vocabularies would have made a movie with `eng` embedded and `en` wanted read as
missing.

## Settled with the owner

**Languages are per library.** Weir has one global list because Weir has
no libraries; that is exactly where Deluno differs. Per library can say "English
on everything, Japanese on anime", and it belongs beside the quality profile,
where "what I want for this shelf" already lives.

**Providers are Connections.** Same shape as indexers and download clients, so
they inherit health, a test button, rate-limit backoff and the "needs you" rules
without any of it being written twice.

**No new colours for subtitles.** A subtitle you have but could improve is
*Upgradable*, one level down — so the bar is a miniature of the dot's ladder,
using the same three colours from the same table:

| Bar segment | Means |
|---|---|
| red | a language you asked for is missing |
| green | you have it, and it could still get better |
| gold | at the cutoff — Deluno has stopped looking |

Nothing new to learn, and it cannot drift, because it reads
`TITLE_MARK_PRESENTATION` like everything else. Two colours are enough until
upgrades exist; gold arrives with them.

**The dot never moves.** DESIGN-001 settled that subtitles do not change a
title's rung, and that stands: a movie short of a language is still Quality met.
Red on the bar means *that extra is missing*; red on the dot means *the title is
missing*. Same word, different subject.

**Weir loses Subber in the same run**, once Deluno's side is proven on the
rig.

## Architecture — the rules this feature is held to

The owner: *"make sure architecturally it's solid, we don't want any overlaps or
overhead or conflicts with any of the other functions."* These are the specific
ways this feature could break Deluno, and what stops each.

### 1. One subtitle store, not two — ADR-001 is explicit

[ADR-001](ADR-001-module-boundaries.md) records that Movies and Series are
parallel copies of one engine, that fourteen repository methods already exist
twice with the same shape, and that the duplication is **"actively
reproducing"** — `GetDailyMetricsAsync` was added to both by copy-paste in a
single session. Step 2 of that ADR is to merge them into `Deluno.Media`.

Adding `movie_subtitle_*` and `episode_subtitle_*` as two hand-written copies
would be adding to the pile Step 2 has to clear, in the same week the ADR was
written.

**So subtitle state is shared from the first line.** `Deluno.Media` already has
the pattern: `MediaTableMap.For(MediaKind)` maps one shared SQL body onto each
catalogue's own database and table names, with the identifiers allow-listed so
nothing interpolates caller input. Subtitles extend that map rather than forking
it.

The one real asymmetry, and it is a fact about the domain rather than a copy: a
movie's subtitle belongs to **the movie**, a show's belongs to **an episode**.
So the map pairs `movie_subtitle_state(movie_id)` with
`episode_subtitle_state(episode_id)` — still one shape and one body, and it is
also exactly what makes a show's bar the sum over the episodes it holds.

### 2. The catalogue page must stay a seek

`CatalogueSearchStateOnPageTests` asserts the query plan, "because nothing about
a wrong plan looks wrong until the twenty-thousandth title". Subtitles must not
be what makes it scan.

- **Movies:** one indexed lookup per row on `(movie_id)`. The eight correlated
  subqueries that DESIGN-001 replaced were expensive because there were eight
  and they disagreed; one indexed range scan per row on a fifty-row page is what
  the existing episode rollup already costs.
- **Series:** the page *already* makes one grouped pass over its own shows for
  `EpisodeCount`, `AiredEpisodeCount`, `AiredWithFileCount`,
  `AiredUpgradableCount` and `NextAirDateUtc`. The subtitle sums were to join
  **that pass**.

  **They do not, and the reason is worth keeping.** *Held* is not one number per
  show. It only counts languages that show's own library asked for, and a page
  can hold two libraries that asked for different ones. Folding it into the
  progress query means either joining the subtitle rows in — which fans all five
  existing counts out and makes every one of them depend on a `DISTINCT` — or
  writing one library's language list into a query that serves both.

  So it is its own grouped pass, over the page's own shows, keyed and indexed the
  same way, and run **once per library present on the page** — which on a
  library-filtered page is always one, and on a shelf nobody has asked for
  subtitles is none. Measured at twenty thousand films
  (`SubtitleScaleBenchmark`): **0.26 ms per hundred-title page**, and **0.014 ms**
  for the read that decides whether to run it at all.
- The query-plan guard is extended to cover the new columns, so a later change
  cannot quietly turn the page into a scan.

### 3. No second scheduler, no second lane, no second worker

Restated because it is the failure this feature is most likely to cause, and
because Weir's Subber ships all three. Searches are planned from the
library's existing cycle, exactly as per-episode search now is
([#303](https://github.com/jampat000/Deluno/issues/303)) — which inherits the
time-of-day window, the manual override and `MaxItemsPerRun` for free, and
cannot drift from them.

Saving a language list therefore **enqueues nothing**. It changes what is
wanted; the cycle decides when to act.

### What "no second scheduler" actually protects, and what it does not

This rule was written twice and got it wrong twice, in opposite directions. Both
corrections came from the owner and both are worth keeping, because the line between
them is the whole of the rule.

**First wrong reading: share everything.** "Planned by the existing cycle" was
built as "planned *inside the existing release-search branch*", which are not the
same sentence. Subtitles inherited the two switches that branch is gated by — so
a library with **Search automatically** off, which that screen describes as *keep
this library manual* and means manual **releases**, asked for English every day
and was never given it, silently. A library with searching on but neither missing
nor upgrade selected did the same. That is precisely Bazarr's audience: a
complete library that wants subtitles for it. Invisible to every test; the rig
found it the only way anything finds this shape — the first end-to-end fetch
could not be made to happen without pressing *search now*.

**Second wrong reading: share the timing.** The fix for the first freed the
switches and left the *clock* borrowed — the interval and the time-of-day window,
both release-search settings. The owner: *"I dont agree that it shares a cycle or
schedule and this was told to you back when I said nothing should be shared or
have to wait for another process or anything."* Right, and the cost was
measurable: on the rig a newly imported episode waited up to a full
`SearchIntervalHours` — twelve hours — before anything even looked at it, while
Bazarr fetches on import.

**So the line is between deciding and waiting.**

| Shared, and should be | Not shared, and must not be |
|---|---|
| The **planner** — one place decides what runs | The **interval**. `SearchIntervalHours` is how often to go back to an indexer, a number chosen for a service that bans you for asking twice. A subtitle pass costs one query when there is nothing to do. |
| The **job lanes and workers** — subtitles ride `import` and `intake`, which already exist | The **window**. A search window keeps you off an indexer at peak; subtitle providers are different servers, with their own rate-limit backoff on the Connection. |
| The **`MaxItemsPerRun` bound** — it means "this many outbound requests per pass", which is the same sentence for both | The **retry delay**. `RetryDelayHours` is an indexer manner. A subtitle absent from every provider is a different fact from a release absent from every indexer. |

Weir's Subber shipped its own lane *and* its own scheduler *and* its own
worker; the left column is what refusing that buys. The right column is what
refusing it must not cost.

**Concretely:**

- `next_subtitle_search_utc`, its own cursor beside the two the release schedules
  use. `next_search_utc` deliberately does not fold it in, because that is what
  the automation screen prints as the next *search*, and a subtitle pass never
  reaches an indexer. A paused library still reads paused.
- A cadence of **five minutes**, which is Deluno's number rather than a setting:
  the standing check asks whether Deluno can decide and explain once, and here it
  can, because unlike an indexer interval there is nothing to tune. The interval
  is not what protects a provider — per-title backoff is.
- **An import makes subtitles due immediately**, so nothing waits on a cadence
  either. One writer, on job completion, which is the single place that already
  knows an import finished. Measured on the rig: import at 04:15:54, `.srt` on
  disk at 04:16:03.
- Whether a shelf wants subtitles is read from the only place that can answer it:
  whether it asked for any languages.

The rule both mistakes are instances of: **one switch, two subjects.**

### 4. Providers are Connections, not a parallel registry

Health, test, rate-limit backoff, credential storage and the "needs you" rules
already exist and are already correct. A `subber_providers` table beside
`indexers` would be a second answer to "is this source working", which is the
`AUDIT-002` defect one layer out.

### 5. Files are written by the code that already owns files

`Deluno.Filesystem` owns paths, imports and probing. A subtitle lands beside its
video through that, not through a private writer with its own idea of where
things go — and **no path mapping**, because Deluno imported the file and knows
where it is.

### 6. Nothing new on the hot read paths

`SubtitleLanguagesWanted` is derived from the library's list, which is a short
string already loaded with the library. `Held` is a count. Neither adds a table
join to the wanted-state path, and neither changes what `titleMark()` returns —
DESIGN-001 settled that subtitles never move the dot, so the mark's inputs are
untouched.

## Build order

**Where this got to. All six are done.** Steps 1 to 5 were recorded in
`b052b66` and the commit after it; step 6 is Weir
[#327](https://github.com/jampat000/Weir/pull/327), and the end-to-end fetch
that gated it ran on the rig first.

1. **Languages, and what you already have.** A per-library list of wanted
   subtitle languages with a cutoff, beside the quality profile, and the two
   catalogue contract fields filled from it. Nothing fetches anything yet — but
   the bars are not all-red either, because the same step reads what the files
   on disk already hold, from beside them and from inside them. A shelf full of
   Bazarr's leftovers lights up green on the day you turn it on, which is the
   truth, and is proof the mark receives it.
2. **Providers as Connections.** One provider end to end (Gestdown or Podnapisi
   — no account needed), with health and a test.
3. **Search and write**, on the existing job lane, planned from the existing
   library cycle the way per-episode search now is.
4. **The remaining seven providers.**
5. **Upgrades and backoff**, reading the same words as release search.

   **Backoff is done** — `movie_subtitle_attempt` / `episode_subtitle_attempt`
   carry `last_search_utc` and `next_eligible_search_utc`, the same two columns
   the wanted state already uses for releases, and the delay starts at the
   library's own `RetryDelayHours`. It was not optional: without it the slice
   took the first `MaxItemsPerRun` rows in whatever order SQLite returned them,
   so a library where five thousand films had no Japanese subtitle asked the same
   ten films every cycle for ever and never reached the rest — while the job
   succeeded, the providers answered, and the bar never moved.

   **No permanent skip**, which is where this parts company with Weir. A
   title that can never be asked again is work that has silently left the system,
   and nobody finds out the day somebody uploads the subtitle. The delay doubles
   and stops at a fortnight.

   **Upgrades are still open, and deliberately.** "Better" is not defined yet:
   this document's own answer is the quality gate — *must match this release* /
   *prefer this release* / *anything readable* — and that is a decision to make
   rather than a scoring model to invent. Until it exists the fetcher takes the
   first usable subtitle from the highest-priority provider, preferring a plain
   track over a hearing-impaired one and never taking a forced one.

6. **Remove from Weir.**

Each step ends with something visible on the rig, which is the only bar that has
counted so far.
