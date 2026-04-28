# Deluno Wiki: Media Operations Workflow

This page defines the user-facing mental model for Deluno. It should stay familiar to Radarr/Sonarr users while explaining where Deluno intentionally improves the workflow.

## Product Position

Deluno is a unified media management app for:

- movies
- TV shows
- indexers
- download clients
- queue monitoring
- import/move/hardlink decisions
- metadata and artwork enrichment
- policy-based routing

Deluno is not trying to replace qBittorrent, SABnzbd, Plex, Jellyfin, or Emby. It orchestrates download clients and prepares media libraries cleanly.

## Familiar Concepts From Radarr/Sonarr

Deluno should preserve these concepts because users already understand them:

- monitored titles
- wanted/missing items
- quality profiles
- custom formats
- indexers
- download clients
- root folders
- categories/labels
- manual search
- automatic search
- import queue
- rename on import
- hardlink/copy/move
- metadata refresh

The difference is that Deluno should make these concepts more explainable and less duplicated across separate apps.

## Core Deluno Concepts

### Media Type

`movies` or `tv`.

This is a structural fact. It should not be represented only as a tag.

### Entity

The exact thing Deluno is managing.

Examples:

- movie: `Dune: Part Two`
- show: `The Bear`
- episode: `The Bear S03E04`

### Library

A configured media collection with a media type, root path, quality profile, search settings, and routing behavior.

Examples:

- Movies
- Movies 4K
- TV Shows
- Anime
- Kids TV

Deluno should allow multiple libraries without requiring multiple app instances.

### Policy Set

A reusable bundle of intent.

Examples:

- Home Theater
- Balanced 1080p
- Anime
- Storage Friendly
- Kids

A policy set can influence quality profile, custom formats, routing, indexers, download clients, and upgrade behavior.

### Tags

Tags are user intent and policy modifiers.

Tags should not be the primary identifier for movie vs TV, nor the primary way completed downloads are matched.

Good tag examples:

- `kids`
- `anime`
- `4k`
- `home-theater`
- `documentary`
- `foreign`
- `remux-only`
- `storage-saver`
- `manual-review`
- `no-upgrade`
- `family`

Tags can influence:

- destination rules
- quality choices
- custom format scoring
- client selection
- indexer selection
- notifications
- upgrade behavior
- manual-review requirements

## Download Client Categories

Categories/labels are how external download clients group active downloads.

Recommended defaults:

- `deluno-movies`
- `deluno-tv`
- `deluno-anime`
- `deluno-movies-4k`
- `deluno-tv-4k`

Users should be able to customize these, but Deluno should generate sensible defaults.

Categories solve this problem:

> qBittorrent/SABnzbd is downloading a movie and a TV episode at the same time. How does Deluno know which is which?

The answer is:

1. Deluno creates a grab record before sending the release.
2. Deluno sends the release with a category/label.
3. The download client reports queue/history by ID/hash/NZB ID and category.
4. Deluno matches the completed item back to its grab record.
5. Filename parsing is only fallback.

## Grab Record

A grab record is Deluno's internal proof of intent.

It should store:

- download client ID
- external client item ID, hash, or NZB ID
- release title
- media type
- target entity ID
- library ID
- policy set ID
- destination rule ID
- quality decision
- custom format score
- category/label sent to the client
- indexer/source
- time grabbed

This prevents Deluno from guessing after the download finishes.

## Download Flow

1. User or automation searches for a release.
2. Deluno scores releases using quality profile and custom formats.
3. Deluno selects a download client using library, policy, tags, and routing.
4. Deluno sends the release to the client with a category/label.
5. Deluno stores a grab record.
6. Client downloads the file.
7. Deluno reads queue/history telemetry.
8. On completion, Deluno resolves the grab record.
9. Deluno previews import destination.
10. Deluno probes media with ffprobe.
11. Deluno imports by hardlink/copy/move.
12. Deluno updates library status, file facts, metadata, and activity history.

## Import Matching Priority

Deluno should match imports in this order:

1. grab record by client item ID/hash/NZB ID
2. download client category/label
3. release name and indexer metadata
4. folder name
5. filename parser
6. manual user selection

Filename parsing is important, but it should not be the first or only source of truth.

## Import Validation

Before importing, Deluno should check:

- source path exists from the Deluno service/container view
- destination path is clear or overwrite is explicitly allowed
- extension is supported
- hardlink is possible or copy fallback is allowed
- ffprobe can parse the file
- file contains at least one video stream
- runtime is not sample-like
- destination rule decision is explainable

If something fails, Deluno should explain the fix in plain language.

Examples:

- "The source file is not visible to Deluno. Check Docker volume mappings."
- "No video stream was detected. This may be a subtitle, sample, or broken file."
- "Hardlink is not possible because source and destination are on different filesystems."

## Metadata vs Media Probe

Metadata answers:

- What movie/show is this?
- What year is it?
- What poster/backdrop/cast/genre/ratings belong to it?
- What external IDs identify it?

Media probing answers:

- What file is this?
- What codec is inside?
- What resolution is it?
- What runtime is it?
- What audio/subtitle streams exist?
- Is it playable enough to import?

Both are required for a reliable media manager.

## Destination Rules

Destination rules are Deluno's replacement for many multi-instance setups.

Examples:

- Movies tagged `4k` go to `Z:\Movies 4K`.
- TV tagged `anime` goes to `Z:\Anime`.
- Kids content goes to `Z:\Family`.
- Documentaries go to `Z:\Documentaries`.
- Foreign-language media goes to `Z:\International`.

Destination rules may match:

- media type
- tag
- genre
- quality profile
- policy set
- studio/network
- original language
- release group
- custom format result

## What Deluno Should Avoid

Deluno should not:

- require multiple app instances for common routing needs
- make users type fragile format strings when a guided selector can work
- rely only on filename parsing
- hide why a release was chosen or rejected
- hide why a file imported to a destination
- duplicate movie and TV settings unnecessarily
- pretend tags are identity
- build its own torrent or Usenet downloader before external orchestration is excellent

## User-Facing Rule

The user should be able to answer these questions from the UI:

- Why was this release picked?
- Why was this client used?
- Why did this file import here?
- Why is this title still wanted?
- Why was this file rejected?
- What quality do I actually have?
- What metadata provider is being used?
- What file facts did Deluno detect?

If the UI cannot answer those questions, the workflow is not finished.

## Future Intelligence

Predictive scoring and visual workflow building are future layers, not replacements for deterministic rules.

See [`deluno-intelligence-and-workflow-builder.md`](deluno-intelligence-and-workflow-builder.md) for the product guardrails:

- deterministic scoring remains the source of truth
- predictive scoring must be optional, bounded, local-first, and explainable
- force override remains available during manual search
- visual workflows should ship as safe presets before becoming a full builder

## Release Decisions

Deluno's release selection should be deterministic before it becomes predictive.

The current decision model separates:

- hard rejection signals, such as samples, trailers, extras, CAM/telesync/workprint/screener releases, or files that later fail media probing
- user-maintained never-grab patterns, configured as plain words or phrases rather than regex
- quality rank and cutoff comparison
- current-file quality delta, so Deluno does not replace a file just because a label looks superficially better
- custom-format score
- seed/peer availability
- size and estimated bitrate sanity
- release group detection
- codec, HDR, hardcoded-subtitle, and other release-name signals

Manual search must always show both the normal grab action and an explicit force override. A forced grab is recorded differently in search history and activity so the user can see that Deluno was told to download anyway.

Force override is not a silent bypass. The UI should ask for a short reason before dispatching a rejected/risky release, then store that reason in search history and Activity. This protects advanced users who know what they are doing without making automation less safe for everyone else.

Never-grab patterns should always be user-editable in Settings. They are global safety rails, not hidden code. Deluno ships sensible defaults for CAM, screener, samples, trailers, and extras, but users can add site-specific junk, language markers, or release groups they never want automated.

Automation only auto-grabs safe `preferred` candidates that meet cutoff and do not downgrade the current file.

Risky or rejected candidates are held for manual review. They remain visible in search history and manual search, but Deluno will not dispatch them automatically. If the user chooses to proceed, the explicit `Force` action records the override separately.

When "unmonitor at cutoff" is enabled, Deluno can stop monitoring a title after import reaches the configured cutoff. This should be recorded as part of import/search history so the user understands why Deluno stopped chasing upgrades.

Import replacement protection is also part of this safety model. If an import would overwrite an existing file, Deluno probes both files where possible and blocks replacements that are clearly worse:

- incoming resolution is lower
- incoming runtime is significantly shorter
- incoming bitrate is substantially lower
- incoming or existing file is missing a video stream

An explicit force replacement path can exist for advanced recovery, but normal automation should not use it.

## Refine Before Import

Some users need a processor between download completion and final import. A common example is cleaning audio/subtitle tracks before the file reaches the permanent library.

Deluno supports this as a separate library workflow:

1. download client finishes the release
2. Deluno marks the item as `Waiting for processor`
3. external processor writes a cleaned file to the configured output folder or posts a processor event
4. Deluno queues `Import queued`
5. normal destination preview, hardlink/copy/move, rename, metadata refresh, and history take over

The important product rule is that this workflow must not bypass Deluno's import logic. The processor creates a cleaner source file; Deluno still owns the final library decision.
