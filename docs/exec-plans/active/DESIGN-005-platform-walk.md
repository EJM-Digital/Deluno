# DESIGN-005 — Walking the arr platforms, and what Deluno should take from them

James: *"I want you to walk Radarr again but this time the whole platform, I want
you to get an idea of how radarr works, looks and performs and I want you to
bring back feedback for deluno."* Then Sonarr, and Prowlarr and Bazarr were on
the same host.

Walked in his live instances: **Radarr** (5,279 movies, 81.8 TiB, 56,798 history
records), **Sonarr** (16 series, 350 episodes, 682 GiB), **Prowlarr** (12
indexers), **Bazarr**. Everything below was seen, not remembered.

## How it performs

At 5,279 movies the poster grid renders the **whole library in one page** — no
paging, an A–Z jump rail down the right edge, and the browser holds all of it.
It is fast to scroll and slow to become interactive; screenshotting it timed out
repeatedly, which is a fair proxy for main-thread cost.

Deluno pages at 100 and keeps one page in memory, which is the better engineering
and the worse *feel*: there is no way to flick to "S". **Deluno needs the A–Z
rail or an equivalent jump**, because keyset paging without one makes a large
library feel further away than Radarr's does. That is the honest trade to fix.

History is 2,840 pages of 20. Paged, and unremarkable.

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
