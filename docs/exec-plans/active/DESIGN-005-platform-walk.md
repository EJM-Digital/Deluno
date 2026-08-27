# DESIGN-005 — Walking the arr platforms, and what Deluno should take from them

James: *"I want you to walk Radarr again but this time the whole platform, I want
you to get an idea of how radarr works, looks and performs and I want you to
bring back feedback for deluno."* Then Sonarr, and Prowlarr and Bazarr were on
the same host.

Walked in his live instances: **Radarr** (5,279 movies, 81.8 TiB, 56,798 history
records), **Sonarr** (16 series, 350 episodes, 682 GiB), **Prowlarr** (12
indexers), **Bazarr**. Everything below was seen, not remembered.

## How it performs — and a correction

At 5,279 movies the poster grid renders the **whole library in one page** — no
paging, an A–Z jump rail down the right edge, and the browser holds all of it.
Screenshotting it timed out repeatedly, which I first read as a problem.

James, who uses it daily, corrected that:

> *"the performance of radarr displaying 5,279 movies is perfectly fine, there
> is the initial 3-5 seconds when going to the URL before it displays but it
> shows a witty message to ease the pain, I dunno if what we do is a better
> experience especially at 6000+ movies."*

He is right, and it inverts the finding. The comparison that matters is not first
paint:

| | Radarr | Deluno today |
|---|---|---|
| First paint | 3–5s, with a message that makes the wait feel deliberate | fast, ~100 titles |
| Reaching title 3,000 | scroll, or click "S" | **30 round trips** |
| Ctrl+F across the library | works | finds one page |
| Scroll to the end | works | 60 clicks at 6,000 |
| Feels like | one library | a database you are querying |

Deluno wins one row and loses four. **Paging was not the safe choice, it was the
wrong one**, and "only this page is kept in memory" is an implementation detail
presented as a feature.

The answer is neither: one continuous **virtualised** shelf, fed by the keyset
query in the background, with an A–Z rail. `library-grid.tsx` already imports
`useVirtualizer`, so only visible rows are ever in the DOM — twenty thousand item
objects is a few megabytes, where twenty thousand DOM nodes is what costs Radarr
its five seconds. Faster than Radarr *and* better than paging. See
[#312](https://github.com/jampat000/Deluno/issues/312).

History is 2,840 pages of 20. Paged, and unremarkable — paging is right there,
because nobody browses history.

## What Radarr has that Deluno has no answer to

### Collections — a first-class object
A page of franchises, each showing *N missing from library*, quality profile,
root folder, genres, synopsis and the member posters. Monitor the **collection**
and sequels are added as they appear. There is a bulk bar for Monitor
Collection / Monitor Movies / Quality Profile / Minimum Availability / Root
Folder / Search on Add.

Deluno has nothing here, and the broker now sends `collection` on every title —
so the data is already arriving.

### Delay Profiles and Release Profiles
- **Delay profile:** preferred protocol, plus a usenet delay and a torrent delay
  — wait N minutes before grabbing so a better release can land first.
- **Release profile:** must-contain / must-not-contain terms, and preferred words
  with scores.

Both are per-tag. Deluno has quality profiles and custom formats but no "wait
before grabbing" and no term-level required/ignored lists.

### Global indexer options that are really acquisition policy
Minimum Age (usenet propagation), Retention days, Maximum Size, Prefer Indexer
Flags, and **Availability Delay** — search this many days before or after the
available date. Per indexer: RSS / Automatic Search / Interactive Search as
three separate toggles.

### Import List Exclusions
A list of TMDb ids that must never be re-added. Deluno's intake has no "never
again" list, so a title you deliberately removed can come straight back on the
next list sync.

### System → Tasks
Eleven scheduled tasks with **interval, last execution, last duration and next
execution**, plus a live queue of recent runs. This is the screen that answers
"what is this thing doing and when will it do it again", and it is exactly the
visibility James asks for when he says routes and functions must not fight for
schedules. Deluno has Activity and a job queue; it does not have this.

### Colour-impaired mode
A UI setting that alters the styling so colour-coded information stays
distinguishable. **Deluno leans harder on colour than Radarr does** — the whole
title-mark system is a coloured dot — so this matters more here, not less.

### Eight notification triggers
On Grab, On File Import, On File Upgrade, On Rename, On Application Update, On
Movie Delete, On Movie File Delete, On Movie File Delete For Upgrade.

### Formatting settings people actually change
First day of week, week column header, runtime format, short/long date format,
time format, relative dates, movie-info language separate from UI language.

## What Sonarr has that Deluno must match

Sonarr is not Radarr with seasons. The differences are structural:

- **Five-state poster colour**, crossing series status with completeness and
  monitoring: Continuing (all downloaded), Ended (all downloaded), Missing
  (monitored), Missing (not monitored), Downloading. Deluno's mark cannot
  express "ended and complete" versus "continuing and complete" at all, because
  it does not store series status — the field the broker now sends.
- **Next airing on the poster** — "Monday", "21 Oct 2026". Deluno computes
  `NextAirDateUtc` and does not show it.
- **Per-season blocks** on the series page, each with a progress badge (`8/8`
  green, `0/0` red), its own size, and its own search / monitor / interactive
  search / manage files / history actions.
- **Sorts (16)** including Network, Next Airing, Previous Airing, Seasons,
  Episodes, Episode Count, Latest Season.
- **Filters (26)** including Episode Progress, **Has Missing Season**, Season
  Count, Seasons Monitored, **Scene Numbering**, **Status**, **Type**
  (Standard / Daily / Anime — which changes episode numbering).

`Has Missing Season` and `Episode Progress` are the two most useful filters in
either app and Deluno has neither.

## What the movie detail page gets right

- Certification badge, year and runtime in the title line.
- **Four rating sources side by side** — TMDb 84%, IMDb 8.8, Rotten Tomatoes
  86%, Trakt 87%. Deluno stores all four and shows one blended number.
- A labelled fact row: path, status, quality profile, size, original language,
  studio, genres.
- A **files table** with video codec, audio info (DTS-HD MA 5.1), size,
  languages, quality, release group, and the **custom format badges with the
  score** (`Remux Tier 01`, `+100`) — the "why this file" story, on the file.
- Subtitle files listed separately with extension and type.
- Cast with photos and character names, then crew.

## Prowlarr — and the one screen worth stealing outright

Twelve indexers. The list carries **Protocol** (torrent/nzb), **Privacy**
(public/private), **Priority**, **Sync Profile**, Added, and **Categories**
(Console / Movies / Audio / PC / TV / XXX / Books / Other). Per-indexer state is
Enabled / Enabled-and-Redirected / Disabled / Error.

**Stats is the screen Deluno should take.** Four tiles — Active Indexers, Total
Queries (9.1K), Total Grabs (34), Active Apps — over four charts:

- **Average indexer response time**, split into queries and grabs
- **Indexer failure rate**
- **Total queries per indexer**, split Search / RSS / **Auth**
- **Successful grabs per indexer**

Nine thousand queries and thirty-four grabs. That ratio is the most useful fact
in the whole application and no other arr surfaces anything like it: it tells you
which trackers are earning their place and which are costing you rate limit for
nothing. Deluno has indexer health — up or down — and nothing that answers *is
this one worth keeping*.

**History** is the raw material: every single query logged with indexer, query
text, parameters, categories, timestamp and **elapsed milliseconds**. 9,123 rows.

**Sync Profiles** name a combination of RSS / Automatic Search / Interactive
Search and apply it per app per indexer. Deluno needs the three toggles; it does
not need the profile, because it has nothing to sync into.

**System → Status** gives version, .NET version, database and *migration number*,
data and startup directories, run mode and **uptime**.

## Bazarr — and what it changes about #301

This is the one that matters most, because Subber is next and DESIGN-002 was
written without walking it.

### The `und` question, answered
DESIGN-002 deliberately refused to guess what an unknown-language subtitle is,
and left the consequence open. **Bazarr does not guess either — it asks once.**
Settings → Languages has *"Treat unknown language embedded subtitles track as…"*
and the same for audio tracks, both defaulting to unset.

That is the right shape and Deluno should adopt it exactly: a setting, empty by
default so `und` counts for nothing, and when set, `und` counts as that language.
It is the same principle as `WantedStatuses.Normalize` refusing to guess — except
here there *is* somebody who can answer, so ask them.

### Things DESIGN-002 does not cover at all

- **"Treat embedded subtitles as downloaded"** — a *toggle*. Deluno always counts
  an embedded track as held. Some people want a sidecar file regardless, because
  players handle the two differently. It should be a choice.
- **Sub-Zero content modifications**, applied after download: strip
  hearing-impaired tags, remove style tags, remove emoji, OCR fixes, common
  whitespace/punctuation fixes, fix all-uppercase, add colour, reverse RTL
  punctuation. A whole category of "make the subtitle usable" work.
- **Whisper as fallback** — ASR-generate a subtitle when no provider has one.
- **Adaptive searching** — skip providers searched recently, with a first-search
  grace window and a repeat interval. This is `next_eligible_search_utc` for
  subtitles, and DESIGN-002 already says backoff should read the same words as
  release search. It should.
- **Score-threshold-driven sync** — only synchronise subtitles scoring *below*
  96 (series) / 86 (movies), with providers excludable from sync, a choice of
  audio-track or embedded-subtitle reference, prefer-original-language-audio,
  framerate-mismatch handling and a max offset. DESIGN-002 says "timing sync" in
  one line; this is what the line costs.
- **Translation**, with its own score and a note added at the start.
- **Custom post-processing command** after download.
- **Language Equals** — user-defined "treat this language as that one, across all
  providers". Deluno normalises codes; it has no user-defined equivalence.
- **Language profiles with must-contain / must-not-contain**, richer than
  Deluno's ordered list plus all/first.
- **Anti-captcha provider** integration, and a per-provider HTTPS-validation
  escape hatch.
- **HI subtitles get their own file extension** (`video.en.sdh.srt`), and there
  is a "single language" mode that omits the code entirely.

### And one thing Deluno already got right
Bazarr's scheduler has *"Update all episode subtitles from disk"* with a **"use
cached embedded subtitles parser results"** toggle, explained as a disk-I/O
trade-off. That is exactly the scan-marker in `movie_subtitle_scan` — read once,
and not again unless the file changes. Independent arrival at the same design,
and Bazarr had to expose it as a setting because re-parsing hurts.

Bazarr's series page also shows **"5 missing subtitles"** as a warning chip and a
per-episode Audio column beside the Subtitles column. Deluno's bar is better
placed — on the poster, where you are already looking — but the audio language
beside the subtitle language is a good pairing Deluno does not have.

## What Deluno already does better, and should keep

- **Quality shown at ladder grain everywhere.** Radarr's poster shows the
  *profile* name ("HD-1080p / 4K-2160p") — the same string on all 5,279 posters,
  which says nothing about the file. Deluno shows what the file *is*.
- **Sorting by quality by ladder rank**, and by **bitrate**. Radarr can sort by
  profile name, which is alphabetical over a list most people have one of.
- **Filtering on the file at all** — Radarr states in its own dialog that it
  cannot.
- **The subtitle bar on the poster.** Bazarr shows coverage as `5/10` in a table
  you have to open a separate application to see.
- **Plain-English explanation** of decisions. Radarr shows a rejection reason on
  a release; it does not explain a schedule, a retry window or a wanted status.

## Ordered, as work

1. **A–Z jump rail** on the library — the one place Radarr's page *feels* better.
2. **Series status, next airing and episode progress** on the TV shelf and in
   filters (data now arriving). Closes the biggest Sonarr gap.
3. **Collections** as an object, with monitor-the-franchise.
4. **System → Tasks**: every scheduled pass, its interval, last duration and next
   run.
5. **Import list exclusions** — "never add this again".
6. **Delay profiles** and term-level release preferences.
7. **The four rating sources**, shown and sortable separately.
8. **Colour-impaired mode**, which Deluno needs more than Radarr does.
9. Date/time/runtime formatting settings.
10. **An indexer scoreboard** — response time, query volume split by kind, failure
    rate, and query-to-grab conversion. Prowlarr's Stats page, which answers "is
    this tracker worth keeping" and which nothing else in the suite attempts.
11. **The Bazarr findings fold into #301** — the unknown-language setting first,
    since it unblocks a question DESIGN-002 left open.
