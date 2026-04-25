# Deluno Capability Map

Updated: 2026-04-21

## Product Goal

Deluno should replace this stack in one coherent product:

- Radarr-style movie management
- Sonarr-style TV management
- Prowlarr/Broker-style indexer and routing management
- Huntarr/Fetcher-style recurring missing and upgrade search
- Cleanuparr-style failed-import cleanup and recovery

The user should not experience that as five products. They should experience Deluno as one premium app with a few clear control centers.

## User-Facing Control Centers

### Movies

Movies should own:

- movie libraries
- movie root folders
- movie release rules
- wanted movies
- ready-for-upgrade movies
- movie import review
- movie failed-import recovery
- recurring movie search options

### TV Shows

TV Shows should own:

- TV libraries
- TV root folders
- show type handling
- season and episode monitoring
- wanted TV episodes
- ready-for-upgrade TV items
- TV import review
- TV failed-import recovery
- recurring TV search options

### Indexers

Indexers should own:

- indexer definitions
- download clients
- service routing
- source health
- search testing
- library-to-service mapping
- release-source visibility

This area replaces the need for a separate Prowlarr or Broker app.

### Activity

Activity should own:

- background search runs
- grabs
- imports
- skips and delays
- failed-import actions
- indexer health changes
- items that need attention

### Settings

Settings should own:

- app-level behavior
- notifications
- storage paths
- defaults
- packaging/runtime preferences

## Internal Engines

The user does not need to see these as separate products, but Deluno still needs them internally.

### Search Automation Engine

Responsibilities:

- recurring missing searches
- recurring upgrade searches
- retry delay and cooldown memory
- search scheduling windows
- max items per run
- fairness across large backlogs

This replaces Huntarr/Fetcher behavior.

### Import Recovery Engine

Responsibilities:

- failed-import classification
- queue-attention detection
- cleanup policy
- safe delete/remove handling
- manual recovery actions
- import review surfaces

This replaces Cleanuparr-style utility behavior and the failed-import parts of Fetcher.

### Source Routing Engine

Responsibilities:

- federated search
- indexer configuration
- download handoff
- source health and testing
- mapping libraries to sources and clients

This replaces Prowlarr/Broker behavior.

## Required Replacement Behaviors

### Movies and TV Shows

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

### Indexers

Deluno must support:

- torrent and usenet sources
- indexer testing
- result previews
- search by media type
- result routing to download clients
- library-aware mappings
- health and failure visibility

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

## Page Architecture

Recommended top-level navigation:

- Overview
- Movies
- TV Shows
- Indexers
- Activity
- Settings

Recommended nested views:

- Movies: Library, Wanted, Upgrades, Import Review
- TV Shows: Library, Wanted, Upgrades, Import Review
- Indexers: Sources, Download Clients, Search, Health
- Activity: Timeline, Queue, Attention

## Backend Module Map

Recommended backend ownership:

- `Deluno.Movies`
  owns movie catalog, movie rules, movie wanted, movie upgrades, movie imports
- `Deluno.Series`
  owns TV catalog, TV rules, show/season/episode state, TV wanted, TV upgrades, TV imports
- `Deluno.Integrations`
  owns indexers, download clients, service mappings, health, federated search
- `Deluno.Jobs`
  owns durable scheduling, activity, cooldown memory, background work state
- `Deluno.Filesystem`
  owns file access, import probing, rename/move/copy coordination
- `Deluno.Platform`
  owns global settings, library definitions, app-level preferences

## Immediate Implementation Priorities

1. Rename and reshape `Connections` into `Indexers`
2. Move recurring search controls into Movies and TV Shows flows
3. Add import recovery as a first-class workflow, not a hidden utility
4. Expand activity wording so indexer, search, import, and recovery work read clearly
5. Build true wanted and upgrade state snapshots for Movies and TV Shows
