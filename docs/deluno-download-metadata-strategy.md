# Download Client and Metadata Strategy

## Download Client Direction

Deluno should support external download clients first. An embedded downloader is a bad near-term bet.

For the user-facing workflow model around categories, grab records, tags, routing, import matching, metadata, and media probing, see
[`wiki-media-operations-workflow.md`](wiki-media-operations-workflow.md).

Mature clients already solve the transport-layer problems well:

- qBittorrent
- SABnzbd
- NZBGet
- Deluge
- Transmission

Those tools already handle protocol behavior, queues, bandwidth limits, repair/unpack, seeding, labels/categories, retries, and history. Deluno should not spend early product effort trying to beat them at transport.

Deluno's differentiated role is orchestration:

- configure external clients cleanly
- test connection and health
- read queue state
- read history and completion state
- map categories or labels by media type and policy
- explain why a release was sent to a specific client
- normalize metrics so every client feels like one system

Supported telemetry adapters should normalize popular clients behind the same queue/history model:

- qBittorrent
- SABnzbd
- Transmission
- Deluge
- NZBGet
- uTorrent-compatible Web UI

The UI should not care which client produced the queue item. It should receive normalized status, progress, speed, ETA, category, source path, error, and import-ready state.

## Normalized Download Model

Every download client adapter should map into one internal model:

- queue item id
- source client id and client type
- title or release name
- media kind
- status
- progress percentage
- download speed
- upload speed where available
- ETA
- size
- downloaded bytes
- category or label
- indexer
- error state
- import-ready state
- completion timestamp where available

Dashboard, activity, system health, and queue pages should read from this normalized model rather than client-specific DTOs.

## Cross-Client Actions

Deluno should expose actions only where they can be implemented consistently or clearly explained:

- pause
- resume
- remove
- retry or requeue
- force import
- open in external client
- test client
- refresh queue

If a client does not support an action, the UI should show why instead of hiding operational detail.

## Embedded Downloader Position

Do not build a BitTorrent engine, Usenet downloader, or half-client as a core requirement.

If Deluno ever embeds downloading, it should be:

- optional
- narrow in scope
- a fallback for simple cases
- separate from the primary architecture

The product line is: Deluno makes external download clients feel like one smart, policy-driven system.

## Metadata Direction

Current state: Deluno stores metadata settings, IDs, and schema space for metadata JSON, but does not yet have a full external metadata provider pipeline.

Near-term target:

- TMDb as the primary metadata source for movies and TV
- IMDb IDs retained as cross-link identifiers
- normalized poster, backdrop, logo, genre, certification, cast, crew, runtime, season, and episode fields
- metadata refresh jobs
- metadata-driven library and detail views

Later additions:

- Fanart.tv for richer artwork
- OMDb where useful for ratings or cross-provider enrichment
- NFO and artwork export as an output layer after ingestion is stable

The metadata model should be centralized. Individual features should consume normalized metadata rather than inventing feature-specific provider shapes.
