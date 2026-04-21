# Deluno Frontend and Backend Map

Updated: 2026-04-21

## Frontend Information Architecture

### Primary Navigation

- `Overview`
- `Movies`
- `TV Shows`
- `Indexers`
- `Activity`
- `Settings`

### Movies Area

Primary jobs:

- understand movie library health
- manage movie rules
- search for missing movies
- review upgrades
- resolve imports and failures

Recommended subviews:

- `Movies / Overview`
- `Movies / Wanted`
- `Movies / Upgrades`
- `Movies / Import Review`

### TV Shows Area

Primary jobs:

- understand TV library health
- manage TV rules
- search for missing episodes and shows
- review upgrades
- resolve imports and failures

Recommended subviews:

- `TV Shows / Overview`
- `TV Shows / Wanted`
- `TV Shows / Upgrades`
- `TV Shows / Import Review`

### Indexers Area

Primary jobs:

- add and test indexers
- manage download clients
- map services to libraries
- run federated search
- inspect source health

Recommended subviews:

- `Indexers / Sources`
- `Indexers / Download Clients`
- `Indexers / Search`
- `Indexers / Health`

### Activity Area

Primary jobs:

- see what Deluno did
- understand what is pending
- find attention items quickly

Recommended subviews:

- `Activity / Timeline`
- `Activity / Queue`
- `Activity / Attention`

## Backend Ownership Boundaries

### Deluno.Platform

Owns:

- library definitions
- app settings
- notifications
- shared storage paths

### Deluno.Movies

Owns:

- movie items
- movie monitoring
- movie release rules
- movie wanted state
- movie upgrade state
- movie import recovery state

### Deluno.Series

Owns:

- show, season, episode state
- TV monitoring
- TV release rules
- TV wanted state
- TV upgrade state
- TV import recovery state

### Deluno.Integrations

Owns:

- indexers
- download clients
- source routing
- connection tests
- health snapshots
- search adapters

### Deluno.Jobs

Owns:

- recurring schedule state
- cooldown logs
- queue state
- activity records
- worker heartbeats

### Deluno.Filesystem

Owns:

- import probing
- rename planning
- move/copy/hardlink execution
- path validation

## Cross-Cutting Engines

### Recurring Search

Lives mostly in:

- `Deluno.Movies`
- `Deluno.Series`
- backed by `Deluno.Jobs`

### Failed Import Recovery

Lives mostly in:

- `Deluno.Movies`
- `Deluno.Series`
- with shared classification helpers where justified

### Federated Search and Routing

Lives mostly in:

- `Deluno.Integrations`

### Activity and Visibility

Lives mostly in:

- `Deluno.Jobs`
- rendered across the frontend

## Current Direction

The current Deluno codebase should evolve toward:

- `Libraries` remaining as an advanced configuration surface
- `Indexers` replacing the current generic connections concept
- recurring search settings surfacing naturally inside Movies and TV Shows
- failed-import recovery becoming part of Import Review rather than a hidden background-only concern
