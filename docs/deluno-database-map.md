# Deluno Database Map

## Purpose

Deluno keeps its storage split across five SQLite databases on purpose.

This is not just an implementation detail. It is part of the product design:

- Movies and TV Shows must stay isolated
- Indexers and shared configuration must stay reusable
- background work must stay durable without creating write contention
- home users must still get a simple backup and restore story

The database layout stays:

- `platform.db`
- `movies.db`
- `series.db`
- `jobs.db`
- `cache.db`

## Ownership Rules

- Each database has one clear owner
- Cross-database joins are not part of the design
- Shared views are composed in application code
- Durable user state lives outside `cache.db`
- Movies never read TV storage directly
- TV never read Movies storage directly

## platform.db

`platform.db` owns app-level setup, library definitions, and Deluno's source-routing configuration.

### Core tables

- `system_settings`
  App-level flags and preferences.
- `root_paths`
  Canonical filesystem roots for movies, TV Shows, downloads, and incomplete downloads.
- `libraries`
  Library definitions, media type, folders, and built-in recurring search settings.

### Integrations and routing

- `indexer_sources`
  Search sources Deluno can use.
- `download_clients`
  Download destinations Deluno can hand off grabs to.
- `library_source_links`
  Which libraries can use which indexers, with optional priority and tag filters.
- `library_download_client_links`
  Which libraries prefer which download clients.
- `app_connections`
  Shared service connections that do not deserve their own top-level control center yet.

### Why it belongs here

These records are shared configuration, not media-domain state. A movie library and a TV library may both route through the same indexer or download client, but they should not duplicate the connection definition.

## movies.db

`movies.db` owns the movie catalog and every movie-specific operational state.

### Catalog and identity

- `movie_entries`
  The base movie catalog Deluno manages.

### Automation state

- `movie_wanted_state`
  Missing, waiting, or upgrade-needed state for each monitored movie per library.
- `movie_search_history`
  Search attempts and outcomes for recurring and manual searches.

### Import recovery

- `movie_import_recovery_cases`
  Current import problems the user can act on.
- `movie_import_recovery_events`
  Timeline of decisions and automatic recovery actions for each case.

### Why it belongs here

Movie eligibility, cutoff decisions, import failures, and recovery history are all movie-domain behavior. They should not be stored in a shared automation database.

## series.db

`series.db` owns the TV Shows catalog and every TV-specific operational state.

### Catalog and identity

- `series_entries`
  The base show catalog Deluno manages.
- `season_entries`
  Per-season structure for monitored shows.
- `episode_entries`
  Episode-level structure, air dates, and file-state basics.

### Automation state

- `episode_wanted_state`
  Missing and upgrade-needed state at the episode level.
- `series_search_history`
  Search attempts and outcomes for recurring and manual TV searches.

### Import recovery

- `series_import_recovery_cases`
  Current TV import problems.
- `series_import_recovery_events`
  Recovery timeline and user actions for each case.

### Why it belongs here

TV needs different data than movies. Seasons, episodes, daily releases, and specials all create a different workflow shape, so TV must keep its own schema instead of pretending it is a nested movie library.

## jobs.db

`jobs.db` owns durable execution state, recurring search coordination, and operational history.

### Queue and workers

- `job_queue`
  Durable pending and in-flight jobs.
- `worker_heartbeats`
  Liveness for running worker loops.

### User-visible activity

- `activity_events`
  Product-friendly activity feed.

### Automation coordination

- `library_automation_state`
  Current scheduler state per library.
- `search_cycle_runs`
  Each recurring or manual library run, including trigger and result counts.
- `search_retry_windows`
  Cooldown memory so Deluno does not hammer the same missing title repeatedly.

### Why it belongs here

This is runtime orchestration state. It should survive restarts, but it is not part of the movies or TV catalog itself.

## cache.db

`cache.db` owns disposable data Deluno can rebuild.

### Planned cache areas

- `provider_payload_cache`
  Cached search or lookup payloads from metadata providers and indexers.
- `provider_etags`
  Freshness and validation metadata for remote calls.
- `search_result_cache`
  Short-lived normalized search results that can be reused within a cooldown window.
- `artwork_cache`
  Artwork lookup metadata and local cache references.

### Why it belongs here

The cache should be safe to delete without losing the user’s library or automation configuration.

## Current Build Direction

The next practical steps for Deluno are:

1. Keep the five-database layout as-is
2. Grow `platform.db` into the true home of Indexers and routing
3. Grow `movies.db` and `series.db` into the true home of wanted state and import recovery
4. Grow `jobs.db` into the true home of recurring search memory and run history
5. Give `cache.db` a real schema so it is no longer just reserved space
