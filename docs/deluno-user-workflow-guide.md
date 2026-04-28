# Deluno User Workflow Guide

Deluno is designed as an orchestration layer for media acquisition, routing, import, and library hygiene. External download clients do the downloading. Deluno owns the decisions, audit trail, import destination, metadata, and recovery path.

## Beginner Setup

1. Create the first user account.
2. Run guided setup.
3. Choose Movies, TV, or both.
4. Pick root folders and a downloads folder.
5. Choose a simple quality target.
6. Choose plain-English release rules.
7. Optionally connect an indexer and download client.
8. Add the first title.

Advanced settings remain available after setup, but the setup flow should create a safe baseline without requiring users to understand Radarr/Sonarr internals.

## Routing And Tags

Tags are not required to distinguish a movie download from a TV download. Deluno distinguishes by media type, library, category, destination rule, and dispatch metadata.

Tags are useful for policy:

- Put anime, kids, 4K, documentary, or foreign-language media in different root folders.
- Route releases to different clients or categories.
- Apply different quality profiles or custom formats.
- Hold certain items for manual review.
- Apply cleanup or Refiner workflows to only some libraries.

## Download Clients

Deluno should support qBittorrent, SABnzbd, NZBGet, Deluge, Transmission, and uTorrent-style Web UI clients through one normalized queue/history model.

Deluno should not try to replace those clients in the near term. The correct split is:

- Download clients handle transfer, repair, unpack, seeding, retry, and bandwidth.
- Deluno handles search, scoring, routing, import, renaming, metadata, recovery, and explanation.

## Search Scoring

Deluno should score releases using more than parsed quality. The scorer should consider:

- Quality tier and whether it is an actual upgrade.
- Cutoff status.
- Custom format score.
- Never-grab patterns.
- Estimated bitrate where available.
- Release group.
- Indexer health and reliability.
- Seeders/peers for torrent sources.
- Size reasonableness.
- Language/subtitle expectations.
- History of previous failed or bad grabs.

Automatic grabs should only happen when the release is safe. Users can still force a release from manual search, and Deluno records why that override happened.

## Refine Before Import

Some users will want an external refiner between download completion and final import.

Workflow:

1. Deluno sends a release to the download client.
2. The download client completes.
3. Deluno detects that the library uses a processor workflow.
4. Deluno waits for the cleaned output folder instead of importing the raw file.
5. The processor removes unwanted audio/subtitles or performs other cleanup.
6. Deluno imports, hardlinks or moves, renames, and refreshes metadata from the cleaned output.
7. If the processor times out or fails, Deluno exposes recovery actions.

## Metadata

Deluno can use direct TMDb/OMDb keys during development and a hosted broker later. The product goal is that most users should not need to bring metadata API keys for normal operation.

Metadata is used for:

- Search matching.
- Posters and backdrops.
- Cast, genres, overview, and dates.
- Ratings from multiple sources.
- Import matching and naming context.

## API Access

External apps should use System -> API to generate keys. Start with the manifest endpoint, then consume health, queue, activity, and import preview endpoints. Processor tools should post processor events so Deluno can coordinate refine-before-import workflows safely.
