# Deluno UI API Contract

## Purpose

This document describes the API shape the premium frontend will need as Deluno moves beyond the initial shell restructure.

The current frontend can use the existing endpoints for a first pass, but the final product requires richer list, detail, and bulk surfaces.

## Movies

### Library

Needed endpoint shape:

- `GET /api/movies`

Final needs:

- paging
- filter arguments
- sort arguments
- bulk summary fields

Fields the UI needs per row:

- id
- title
- year
- monitored
- status
- current quality
- target quality
- quality cutoff state
- has file
- root folder
- tags
- genres
- last search
- next retry
- created date

### Wanted

Current endpoint:

- `GET /api/movies/wanted`

UI needs:

- total wanted
- missing count
- upgrade count
- waiting count
- recent items

Later needs:

- pagination
- filtering by reason
- separate missing and upgrades endpoints or query support

### Search History

Current endpoint:

- `GET /api/movies/search-history`

UI needs:

- title context
- trigger kind
- outcome
- release name
- indexer name
- details payload
- created timestamp

### Import

Current endpoint:

- `GET /api/movies/import-recovery`

UI needs:

- issue counts by type
- recent cases
- future resolution status

### Bulk Actions

Planned endpoints:

- `POST /api/movies/bulk/monitor`
- `POST /api/movies/bulk/profile`
- `POST /api/movies/bulk/root-folder`
- `POST /api/movies/bulk/tags`
- `POST /api/movies/bulk/search`
- `POST /api/movies/bulk/rename`

## TV

### Library

Current endpoints:

- `GET /api/series`
- `GET /api/series/inventory`

Final needs:

- paging
- filter arguments
- sort arguments
- richer status fields per series

Fields needed per series row:

- id
- title
- year
- monitored
- continuing or ended
- network
- profile
- root folder
- season count
- tracked episode count
- missing episode count
- quality state
- last search
- next retry
- tags

### Series Detail

Needed endpoints:

- `GET /api/series/{id}`
- `GET /api/series/{id}/episodes`
- `GET /api/series/{id}/history`
- `GET /api/series/{id}/activity`
- `GET /api/series/{id}/import-recovery`
- `GET /api/series/{id}/search-history`

Episode row fields needed:

- id
- season number
- episode number
- title
- air date
- monitored
- has file
- quality
- wanted status
- search eligibility
- import issue state

### Wanted

Current endpoint:

- `GET /api/series/wanted`

Final needs:

- episode-aware wanted surfaces
- split missing and upgrades
- pagination
- filter by monitored and season

### Import

Current endpoint:

- `GET /api/series/import-recovery`

Final needs:

- episode-level import issue details
- parse diagnostics
- resolution state

### Bulk Actions

Planned endpoints:

- `POST /api/series/bulk/monitor`
- `POST /api/series/bulk/profile`
- `POST /api/series/bulk/root-folder`
- `POST /api/series/bulk/tags`
- `POST /api/series/bulk/search`
- `POST /api/series/bulk/season-monitoring`
- `POST /api/series/bulk/rename`

## Indexers

### Providers

Current endpoints:

- `GET /api/indexers`
- `POST /api/indexers`
- `POST /api/indexers/{id}/test`

Future needs:

- update
- enable and disable
- priority reorder
- health detail

### Routing

Current endpoints:

- `GET /api/libraries`
- `GET /api/libraries/{id}/routing`
- `PUT /api/libraries/{id}/routing`

UI needs:

- effective routing preview
- library to provider links
- library to download client links
- priority
- tag conditions

### Download Clients

Current endpoints:

- `GET /api/download-clients`
- `POST /api/download-clients`

Future needs:

- update
- test
- enable and disable
- category preview

## Activity

### Queue

Current endpoint:

- `GET /api/jobs?take=n`

Final needs:

- category filtering
- related entity expansion
- status filters
- pagination

### History

Current endpoint:

- `GET /api/activity?take=n`

Final needs:

- event category filtering
- imports-only
- failures-only
- entity links
- pagination

## Settings

### Libraries

Current endpoints:

- `GET /api/libraries`
- `POST /api/libraries`
- `POST /api/libraries/{id}/import-existing`
- `POST /api/libraries/{id}/search-now`

Future needs:

- update library
- delete library
- test path
- richer storage metadata

### Media Management

Current source:

- `GET /api/settings`

Future needs:

- dedicated media-management settings payload
- naming formats
- import behavior settings
- cleanup and link strategy

### Quality

Current endpoint:

- `GET /api/quality-profiles`

Future needs:

- create profile
- update profile
- delete profile

### Search

Current source:

- per-library fields from `GET /api/libraries`

Future needs:

- dedicated global search policy
- defaults versus per-library overrides

### General

Current endpoints:

- `GET /api/settings`
- `PUT /api/settings`

## Realtime

Planned SignalR surfaces should eventually push updates for:

- queue changes
- activity timeline changes
- dispatch changes
- import issue changes
- wanted state changes
- series inventory changes

The premium UI should not rely exclusively on polling once those hubs are available.
