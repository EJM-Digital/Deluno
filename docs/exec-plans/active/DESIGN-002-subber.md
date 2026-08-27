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

### Ports from Weir

Eight provider clients, the search-and-write service, upgrades, backoff,
hearing-impaired exclusion.

### New, and worth it

- **A language profile with a cutoff**, per library, beside the quality profile.
  Ordered — English, then Spanish — and a cutoff that says *stop here, this is
  good enough*, which is exactly how quality already behaves in Deluno. One idea
  learnt once.
- **A quality gate on the subtitle itself.** Bazarr expresses this as numeric
  score weights, which nobody can reason about. Deluno should say what it means:
  *must match this release* / *prefer this release* / *anything readable*, with
  the consequence written underneath.
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

## Build order

1. **Languages, and nothing else.** A per-library list of wanted subtitle
   languages with a cutoff, beside the quality profile, and the two catalogue
   contract fields filled from it. Nothing fetches anything yet — the bars light
   up all-red because you have asked for languages and hold none, which is the
   truth, and is proof the mark receives it.
2. **Providers as Connections.** One provider end to end (Gestdown or Podnapisi
   — no account needed), with health and a test.
3. **Search and write**, on the existing job lane, planned from the existing
   library cycle the way per-episode search now is.
4. **The remaining seven providers.**
5. **Upgrades and backoff**, reading the same words as release search.
6. **Remove from Weir.**

Each step ends with something visible on the rig, which is the only bar that has
counted so far.
