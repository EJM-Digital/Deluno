# DESIGN-004 — Filtering past what Radarr can do

James: *"I want people to have so much granular things that they can do its not
funny. In my opinion the radarr filtering choices are a little limited."*

So this starts with what Radarr actually offers, walked in his own instance —
5,279 movies, 81.8 TiB, 24 custom filters he built himself — rather than from
memory.

## What Radarr has

**Poster options (14):** poster size, detailed progress bar, title, monitored,
quality profile, cinema release date, digital release date, physical release
date, release date, TMDb rating, IMDb rating, Tomato rating, Trakt rating, tags,
search-on-hover.

**Views (3):** Table, Posters, Overview.

**Sorts (21):** Monitored/Status, Title, Studio, Quality Profile, Added, Year,
In Cinemas, Digital Release, Physical Release, Release Date, TMDb Rating, IMDb
Rating, Tomato Rating, Trakt Rating, Popularity, Path, Size on Disk,
Certification, Original Title, Original Language, Tags.

**Custom filter fields (33):** Added, Certification, Collection, Considered
Available, Digital Release, Genres, IMDb Rating, IMDb Votes, In Cinemas,
Keywords, Minimum Availability, Monitored, Original Language, Original Title,
Path, Physical Release, Popularity, Quality Profile, Release Date, Release
Groups, Release Status, Runtime, Size on Disk, Studio, Tags, Title, TMDb Rating,
TMDb Votes, Tomato Rating, Trakt Rating, Trakt Votes, Year.

**Operators:** equal, not equal, greater than, greater than or equal, less than,
less than or equal — plus contains / does not contain / starts with / ends with
on text and list fields.

Deluno today: **6 filter fields, 9 sorts, 11 poster options, 2 views.** On the
raw count we are behind, and that is the first thing to fix.

## The sentence that decides the strategy

Radarr prints this in its own Custom Filters dialog:

> **"Filters are available only for the properties of a movie, they are not
> available for properties of the file(s) you may have for that movie."**

That is Radarr telling you where its ceiling is. Thirty-three fields, and not
one of them is about the file — no codec, no audio layout, no release group of
the copy you hold, no bitrate, no container, no subtitle track, no file age. Its
"Size on Disk" and "Quality Profile" are movie-level properties in its schema,
not facts read from the file.

**Deluno does not have that ceiling.** `movie_wanted_state` already stores
`video_codec`, `audio_codec`, `audio_channels`, `release_group`, `file_path`,
`file_size_bytes`, `current_quality`, `imported_utc`, `last_search_utc`,
`next_eligible_search_utc` and `last_search_result`, and `movie_subtitle_state`
stores every subtitle language, source and variant. Deluno already filters by
quality and size *from the file*, which Radarr says it cannot do at all.

So the plan is not "add the missing 27 fields". It is: **close the gap on the
title, then open a gap on the file, on time, and on what Deluno decided.**

## Four axes, in order

### 1. Parity on the title (closes the count gap)

Certification, collection, studio, original language, original title, keywords,
tags, path, the four rating sources with their vote counts separately, the three
release dates plus minimum availability and considered-available, quality
profile, release status, added-date.

Most arrive free: the metadata blob now carries certification, studio, network,
collection, original language, status and director (`f22f5a3`), and the adapters
can finally read them (`9ea68fa`). What they need is columns to filter on, which
is a migration, not a discovery.

### 2. The file — where Radarr stops

- **Codec**, **audio codec**, **audio channels** — stored, displayed, never
  filterable.
- **Release group of the copy you hold** — Radarr filters the *release groups
  you have configured*, not the group of your actual file.
- **Bitrate** — already sortable (`6477911`); the filter is the same expression.
- **Container / extension**, **file age** (imported when), **path depth**.
- **Subtitles** — held languages, missing languages, forced-only, embedded
  versus external, unknown-language sidecars. Nothing else in this space can ask
  any of it.

### 3. Time, relative

Radarr's date filters take absolute dates, so "added last month" is a filter you
have to rewrite every month. Deluno should take **relative** values: *added in
the last 30 days*, *digital release within 14 days*, *not searched in 90 days*,
*imported before 2024*. A saved view built on a relative date stays true.

### 4. What Deluno decided — nobody else has this at all

Deluno records its own reasoning, and none of it is askable:

- **Search state:** last search result, next eligible search, currently
  retry-delayed, never searched, searched N times with no grab.
- **Wanted reason:** the sentence Deluno wrote about why a title is on the list.
- **Policy conformance:** *below the size rule for its own quality tier* —
  a 2160p file at 4 GB — or *above it*. A question about the file measured
  against the profile it was accepted under, and the single most useful audit a
  media library can run. Nothing in the arr suite asks it: Cleanuparr is about
  stalled, slow and orphaned *downloads*, not about whether the files you
  already keep still match the rules you set. Today this is a spreadsheet.
- **Duplication:** held in two libraries, two files for one title.
- **Filesystem truth:** tracked file missing from disk.

## Two structural things to do better

**OR, and grouping.** Radarr ANDs its rows and offers nothing else, so "Horror
*or* Thriller, released before 1990" is not expressible — it takes two saved
filters and a human. Deluno should support groups with AND/OR between them.

That reopens the argument DESIGN-003 settled — the generic rule engine deleted
in #302 could express filters nothing could answer. The resolution is that the
**fields stay a closed, typed, server-known set** and only the *combination*
becomes free-form. A grouped expression over a fixed vocabulary cannot ask an
unanswerable question; the 45-value `FilterField` union could, because half its
values named things nothing set.

**A saved filter should be able to do something.** Radarr's custom filters are
view-only — they change what you look at and nothing else. Deluno already plans
work from a library cycle, so a saved filter is one step from being a *scope*:
"search everything matching this, nightly", "apply this quality profile to
everything matching this", "unmonitor everything matching this". That is the
leap from a filter to a rule, and it is where this stops being a better Radarr
and starts being the thing that replaces it.

## Cost, and the rule that keeps it honest

Every filter is a WHERE clause on an indexed, stored column, and every sort has
an index behind it. That is not negotiable — `CatalogueSearchStateOnPageTests`
and `Sorting_by_the_file_stays_an_index_walk` exist because a wrong plan looks
perfect until the twenty-thousandth title, and James's own library is five
thousand.

Fields on the wanted state need the V0016/V0017 treatment: the picked file's
value cached on the title's row, maintained by a trigger, so a filter on it is
an index lookup rather than a correlated pick per row.

And the rule that has held all the way through: **a page asking for nothing runs
exactly the query it ran before any of this existed.**
