# DESIGN-008 — Retention: Leaving soon, then gone

> **Status: settled** with the owner on 2026-09-17. The decisions in the table were
> each put to him as a question and answered. The engineering below them (the
> step storage, three-valued answers, operators, the order a check runs in) is
> how those decisions are carried out. Tracking: epic #492, rules and lifecycle
> #494, server collections #521, Seerr #522.

The owner, on what Pruner is:

> "Pruner is a simpler version of maintainerr and needs to have the same
> capabilities."

And on how the pieces fit:

> "Deluno manages the media library -> emby/plex/jellyfin allow users to
> interact with that library. They all need to work together to achieve the
> same goal."

---

## What shipped first, and why it is not enough

#518 gave Deluno media server connections (#493) and a first cut of retention
rules: a flat list of conditions that must all hold, two server fields (watched,
last played), a preview, and a cleanup that removes through Deluno's Remove path
and leaves an exclusion with the reason.

Against [Maintainerr](https://docs.maintainerr.info/3.4.1/glossary/) it is
missing: OR, most fields, seasons and episodes, a wait before removal that
viewers can see, per-title protection, and Seerr. What it got right stays:

- **Silence is not permission.** `MediaServerAnswer` keeps "could not ask"
  apart from "nothing matched" in the type system.
- Matching server items to Deluno titles by path, then by provider id.
- Removal through `TitleRemovalService`, files to the recycle bin, and a
  retention exclusion carrying the rule, the date and the reasons. Never a
  release blocklist entry.
- Servers told the files are gone (`/Library/Media/Updated`, Plex rescan), never
  their own delete call.

## The lifecycle

```
            rule check (every N hours, or Run now)
                          │
       matches, not kept  ▼
   ┌──────────────► Leaving soon ──── stops matching ───► (gone from the list)
   │                 │   │  └──────── Keep ─────────────► Kept
   │   removal date  │   │
   │   reached,      │   └── server needed cannot be asked ─► held (retry next check)
   │   still matches ▼
   │   automatic ─► Removed        ask first ─► Ready to remove ─► confirmed ─► Removed
```

| # | Decision | the owner's answer |
|---|---|---|
| 1 | Which titles a rule can act on | Only titles Deluno manages. Items on a server that Deluno does not manage are **listed** ("On your servers but not in Deluno") and **never touched**. |
| 2 | Wait before removal | Per rule, **1–365 days, default 14**. Zero is not allowed: there is always a warning. |
| 3 | Where the wait is visible | One **"Leaving soon" collection per media type** on every server a rule uses (#521). Deluno fills it; one-way. |
| 4 | What saves a title | Being **watched** (the rule stops matching), **favourite / watchlist / requested-by as rule fields** (not automatic), and a **Keep** button. |
| 5 | What Keep covers | **Chosen each time**: this rule, or all rules, with an optional note. Shown on the title's page and the rule's Kept list; undoable. |
| 6 | Removal day | Per rule: **automatic** (default for new rules) or **ask first** (Ready to remove, confirmed in Deluno). Always re-checked first. |
| 7 | How often | Per rule, **every 1–168 hours, default 8**, plus Run now. |
| 8 | Whole movie or show removed | Files deleted, servers told, title removed from Deluno, exclusion with rule, date and reasons, Seerr media record cleared (#522). |
| 9 | Season or episodes removed | Only those files. The show stays; the episodes are marked **removed by rule** with the reason and date and are no longer wanted. Undoable per season or episode. |
| 10 | Seerr | **Added now**: request fields for rules, and removal deletes Seerr's media record. Proven on a lab Jellyseerr on the rig. |
| 11 | Tautulli | **Skipped**: Deluno reads watch history from the servers directly. |
| 12 | Notifications | **Entered Leaving soon**, **Removed**, **Waiting for you**, **Failed**. |
| 13 | Tracking | #492 rewritten, #494 reopened, #521 and #522 opened. |

### A title's facts at the moment of removal

A removal is never decided from a stale list. On the removal date the rule is
evaluated again, exactly as on any check, and the title is removed only if:

- it still matches;
- it is not kept (for this rule or for all rules);
- every server the rule reads answered this check.

If a server could not be asked, the title is **held**: nothing is removed, the
Leaving soon mark stays, and "Waiting for you" says which server and since when.

### What a check does, in order

1. Evaluate the rule (below). If a server it reads could not be asked, record
   the check as held and stop: no marks are added, cleared or acted on, because
   Deluno does not know.
2. For each title that matches and is not kept: mark it Leaving soon if it is not
   already, with the removal date = now + the rule's wait.
3. For each existing mark whose title no longer matches: clear it, recording why
   ("no longer matches", or which step it now fails).
4. For each mark whose removal date has passed: automatic rules remove it; ask
   first rules move it to Ready to remove and notify.
5. Record the check in history: marked, cleared, kept, ready, removed, held,
   failed, and the space freed.

A title can be marked by more than one rule. It is removed by the first rule
whose date arrives, and the reason names that rule.

## Rules

### The builder

Maintainerr's step model, made readable (the owner: *"simpler and more visually
easier to configure and also ensure same feature rich rules"*):

```
Rule: Unwatched films                          Movies
Start with every movie in Deluno                1,240

  Keep only    Watched by  is  nobody        ▸ 412 left
  Keep only    Added   more than 90 days ago ▸ 301 left
  ┌ Also include (block: Low rated) ──────────────┐
  │ Keep only  Watched by  is  everyone           │
  │ Keep only  IMDb rating  below  5.0            │
  └───────────────────────────────── ▸ +27 ───────┘
                                              ▸ 328 match
  [+ Keep only…]  [+ Also include…]  [+ Block]
```

- **Keep only** (AND) narrows the titles so far to those the condition holds for.
- **Also include** (OR) adds titles from the whole library that the condition
  holds for.
- A **block** is brackets: its own steps run from the whole library, and its
  result is kept-only or also-included as one step.
- A live count after every step.
- Each matched title lists the steps that put it there, in sentences.

Stored as data (`steps_json`), so a new combination is configuration:

```json
[
  { "type": "condition", "op": "keep", "field": "server.watchedBy", "operator": "none", "value": null },
  { "type": "condition", "op": "keep", "field": "deluno.added", "operator": "olderThanDays", "value": 90 },
  { "type": "block", "op": "include", "name": "Low rated", "steps": [
      { "type": "condition", "op": "keep", "field": "server.watchedBy", "operator": "all", "value": null },
      { "type": "condition", "op": "keep", "field": "server.ratingImdb", "operator": "lessThan", "value": 5.0 }
  ] }
]
```

Saved rules from #518 migrate to a run of Keep only steps, which means exactly
what they meant before.

### Three answers, not two

A condition on a title is **true**, **false** or **cannot say** — a server that
cannot provide the field (Plex has no per-user favourites; Emby has no
watchlist), or a value that is simply absent. *Cannot say* never matches, in
either direction: a Keep only step drops the title, an Also include step does
not add it, and the title is listed as undecided with the reason. It is never
read as false, because `not favourited` would then remove everything on a server
that cannot tell.

A field that **none** of a rule's servers can provide is refused when the rule is
saved, naming the field and the servers.

### Levels

Movies; shows, seasons and episodes. A season or episode inherits its show's
fields (genre, network, tags…) and adds its own (season number, episode number,
file size, quality, air date, last viewed…).

### Fields

One name per field. Server fields read from the servers chosen on the rule; a
title counts as watched if **any** of them says so. Where a server cannot provide
a field the builder marks it for that server.

**Watching (from Emby, Jellyfin, Plex)**

| Field | Kind | Emby / Jellyfin | Plex |
|---|---|---|---|
| Watched by | users | per user | token's account only |
| Times viewed | number | per user play count | viewCount |
| Play attempts (unfinished) | number | playback position set, not played | unsupported |
| Last viewed | date | per user LastPlayedDate | lastViewedAt |
| Favourited by | users | per user IsFavorite | unsupported |
| Watchlisted by | users | unsupported | plex.tv watchlist |
| All episodes seen by *(shows, seasons)* | users | from episodes | token's account |
| Watched episodes *(shows, seasons)* | number | from episodes | viewedLeafCount |
| Newest episode viewed *(shows, seasons)* | date | from episodes | from episodes |
| Total views *(shows, seasons)* | number | from episodes | from episodes |
| Watchers *(shows, seasons)* | users | from episodes | token's account |

**Server metadata**

| Field | Kind |
|---|---|
| Date added to the server | date |
| Release date | date |
| User rating, critic rating, audience rating | number (1–10) |
| IMDb, Rotten Tomatoes critic and audience, TMDb rating | number (1–10), where the server carries them |
| People, genres, labels/tags | text list |
| Collections, playlists (names and counts) | text list / number |
| Video resolution, bitrate, video codec | text / number |
| Last episode added, last episode aired *(shows, seasons)* | date |
| Available episodes *(shows, seasons)* | number |

**Deluno** (in place of Radarr and Sonarr): every catalogue filter field
(DESIGN-003/004) — title, genre, tag, year, ratings and votes, runtime, quality,
size, bitrate, codecs, audio channels, release group, path, imported, added,
certification, original language, keywords, cutoff met, profile, monitored,
studio, collection, network, series status, episodes held and aired… — plus
**free space** and **total space** of the drive the title's library lives on.

**Seerr** (#522): requested by, request date, approval date, is requested,
times requested.

### Operators by kind

| Kind | Operators |
|---|---|
| number | above, below, equals, not equals, is set, is not set |
| date | before, after, within the last N days, more than N days ago, within the next N days, is set, is not set |
| text | is, is not, contains, does not contain |
| text list | includes any of, includes all of, includes none of, is empty |
| users | anyone, nobody, everyone, includes any of, includes none of |
| yes/no | is yes, is no |

## Seasons and episodes removed by rule

A season or episodes removed by a rule are not a title removal:

- only those tracked files go to the recycle bin;
- each episode gets a **removed-by-rule** record (rule, date, reasons) and is
  unmonitored, so it is not wanted again;
- the episode list shows *Removed by rule “Watched seasons” on 3 Oct 2026*;
- **Undo** deletes the record and monitors the episodes again, so they become
  wanted.

## Screens

Retention lives where it is today (`/search-cycles/retention`), in the
list → drawer grammar:

- **Rules** — name, level, servers, wait, removal, next check, counts. The
  drawer holds the step builder with live counts, servers, wait, removal and
  interval, and Run now.
- **Leaving soon** — title, rule, why, removal date, size; Keep on each row.
  Ready to remove rows carry Remove now.
- **Kept** — title, this rule or all rules, note, when; Undo.
- **History** — each check, what it did, space freed.
- **On your servers but not in Deluno** — a count and a list, with a way to bring
  them into Deluno.

A title's own page says when it is Leaving soon (rule, why, date, Keep) or
Kept (note, Undo). Title cards do not change: DESIGN-001 keeps one dot and one
bar on a card.

## Where it is proven

On the rig only, never the owner's own servers: the lab Jellyfin and Emby, a lab
Jellyseerr on its own port and data folder, and Plex and Overseerr against fakes
in tests.

## Build order

1. **Rules and fields** (#494): step builder with live counts, the field
   catalogue with three-valued answers, richer server reads, all four levels.
2. **Lifecycle** (#494): Leaving soon marks, Keep, removal automatic or ask
   first, per-rule checks, history, notifications, unmanaged listing, the
   removed-by-rule record for episodes, and the screens.
3. **Server collections** (#521).
4. **Seerr** (#522).

## What a rule does on the day, and the hand-made edges (#1008 to #1012)

These come from the parity decision sheet (G174 to G178); each was checked
against Maintainerr's source by name (`collection-handler.ts`,
`collection-worker.service.ts`, `collections.service.ts`, `ServarrAction`).

- **Expiry action** (#1011). Per rule: remove (what every rule did until now,
  and what an existing rule keeps), stop monitoring (files stay), only write it
  down in Activity, or give the title a Deluno tag. Deluno is the Sonarr/Radarr
  here, so Maintainerr's "tag in the arr" is a Deluno tag through
  `ITitleTagService`; a season or episode gives its show the tag. A title whose
  action was done is marked Done and left alone until it stops matching or is
  kept; changing the rule's action starts it afresh after a new wait. Only a
  removing rule puts anything in a server's Leaving soon collection. A new rule
  starts on "only write it down".
- **Wait while playing** (#1012). Before a removal (never before the other
  actions) Deluno asks every enabled media server, not only the ones the rule
  reads, what is being played (Plex `/status/sessions`, Jellyfin and Emby
  `/Sessions`), because the files go for all of them. A play matches the title
  by the server's own item, season or show id on a server the rule reads, by the
  file being played (path-normalised, case-insensitive) on any server, or for a
  film by its tmdb, imdb or tvdb id. A title in use is held and tried at the
  next check, with an Activity line. Maintainerr treats a failed lookup as "nothing playing"; Deluno
  does not: if a server cannot say, nothing is removed. An existing rule keeps
  removing as before; a new rule starts with the wait on. It also covers a
  person confirming a removal.
- **Keep in Deluno only** (#1009). A per-rule switch: nothing is created on a
  media server for that rule's list. Off for every existing rule.
- **Add by hand** (#1008). Add a title to a rule's list from Leaving soon; it
  is marked Manual, waits the rule's days, and the steps no longer decide
  whether it stays. Taking a title off by hand is a Keep from that rule, which
  keeps the rule from putting it back.
- **Postpone** (#1010). Push a title's day back N days (1 to 3650) without
  keeping it; past its day it counts from today. It only ever moves a day later.
