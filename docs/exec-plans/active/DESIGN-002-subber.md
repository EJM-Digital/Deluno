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

## Open questions for the owner

1. **Where do languages get asked for?** Per library, the way a quality profile
   is? Per title? Both? Weir had one global list, which cannot express
   "English on everything, Japanese on anime" — and Deluno already has the
   Library as the place a preference like this belongs.
2. **Providers as Connections, or their own thing?** Indexers and download
   clients are Connections with health, test buttons and credentials. Eight
   subtitle providers look like the same shape, and would inherit health
   checking and the "needs you" rules for free.
3. **Does a subtitle count as an upgrade?** Deluno has *Upgradable* for a title
   below its quality profile. A subtitle that could be better is a different
   axis — DESIGN-001 already settled that subtitles never change the dot, so
   this is about where an upgrade appears, not what colour it is.
4. **Weir removal** — same PR, or after Deluno's side is proven on the rig?

## Build order (proposed)

1. **Languages, and nothing else.** Wherever question 1 lands, plus the two
   contract fields filled from stored state. The bars light up with a number
   nobody has fetched yet — proof the mark receives it.
2. **Providers as Connections.** One provider end to end (Gestdown or Podnapisi
   — no account needed), with health and a test.
3. **Search and write**, on the existing job lane, planned from the existing
   library cycle the way per-episode search now is.
4. **The remaining seven providers.**
5. **Upgrades and backoff**, reading the same words as release search.
6. **Remove from Weir.**

Each step ends with something visible on the rig, which is the only bar that has
counted so far.
