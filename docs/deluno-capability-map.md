# Deluno Capability Map

Updated: 2026-05-13

## Product Goal

Deluno should replace this stack in one coherent product:

- Radarr-style movie management
- Sonarr-style TV management
- Prowlarr/Broker-style indexer and routing management
- Recyclarr-style guide-backed quality, naming, and custom-format posture
- Huntarr-style recurring missing and upgrade search
- Fetcher-style monitored-missing, cutoff-upgrade, run-state, and failed-import cleanup automation
- Cleanuparr-style failed-import cleanup and recovery

The user should experience one premium app, not a stitched toolchain.

## Current Product Shape

Already visible in the codebase today:

- separated Movies and TV engines
- authenticated single-user application flow
- libraries, routing, quality profiles, tags, custom formats, destination rules, and policy sets
- wanted and import-recovery views for Movies and TV
- operational queue/activity surfaces
- indexer and download-client setup, test, enable/disable, and health flows
- normalized download-client telemetry with persisted last-known snapshots
- custom-format dry-run evaluation
- external processor coordination for refine-before-import

Still incomplete or in-flight:

- richer search automation ownership and UI
- deeper upgrade and replacement protection explainability
- broader realtime operational coverage
- fully mature multi-library and guided preset workflows
- cleaner separation of newly expanded platform services

## User-Facing Control Centers

### Overview

Overview should summarize:

- system health
- queue state
- active downloads and imports
- wanted and upgrade pressure
- operational attention items

### Movies

Movies should own:

- movie libraries
- movie release rules
- wanted movies
- upgrade candidates
- metadata linking and refresh
- import review and failed-import recovery
- movie bulk actions

### TV Shows

TV Shows should own:

- TV libraries
- show, season, and episode monitoring
- wanted episodes and upgrade candidates
- episode-aware search flows
- metadata linking and refresh
- import review and failed-import recovery
- TV bulk actions

### Indexers

Indexers should own:

- indexer definitions
- download clients
- service routing
- source and client health
- search testing
- library-aware mappings
- release-source visibility
- normalized telemetry and queue actions

### Activity

Activity should own:

- background search runs
- grabs
- imports
- skips and delays
- failed-import actions
- health changes
- items that need attention

### Settings

Settings should own:

- app-level behavior
- libraries and workflow defaults
- quality, tags, custom formats, policy sets, and destination rules
- notifications and storage paths
- migration/import assistance
- API keys and external automation posture

## Internal Engines

These do not need to appear as separate products in the UI, but they should remain explicit internally.

### Search And Decision Engine

Responsibilities:

- federated search
- custom-format matching
- quality/profile evaluation
- result scoring and explanation
- per-library routing-aware acquisition decisions

### Intake And Import Engine

Responsibilities:

- import lists and watchlist-style syncing
- refine-before-import processor coordination
- destination resolution
- cleanup and recovery classification
- manual recovery and requeue flows

### Routing And Integration Engine

Responsibilities:

- library-aware source routing
- indexer configuration and testing
- download-client configuration and testing
- telemetry normalization
- webhook ingestion
- client grab and queue actions

### Operational Visibility Engine

Responsibilities:

- queue and activity state
- health views
- logs and job visibility
- realtime updates
- explanation trails for decisions and failures

## Required Replacement Behaviors

### Movies And TV

Deluno must support:

- add and monitor items
- manual search
- search on add
- automatic grabs from fresh releases
- recurring missing search
- recurring upgrade search until cutoff
- clear wanted and upgrade views
- import and rename
- failed-import review and cleanup
- multiple libraries per media type

### Indexers And Clients

Deluno must support:

- torrent and usenet sources
- source and client testing
- result previews
- search by media type
- result routing to download clients
- library-aware mappings
- health and failure visibility
- normalized telemetry and queue actions

### Import Recovery

Deluno must classify and act on failed imports in a way users can understand:

- quality rejected
- unmatched and needs review
- sample release
- corrupt
- download failed
- import failed

Each class should have:

- a count
- a plain-language explanation
- a user policy
- a safe action

## Product Direction

Near-term direction should continue to push toward:

1. stronger operational workflows inside Movies and TV instead of pushing users back into generic settings
2. broader realtime coverage so queue/import/health surfaces do not depend on page reloads
3. better explanation of search, quality, and routing decisions
4. guided presets and multi-library coordination that feel native instead of bolted on
5. cleaner code ownership for the expanding platform API surface
