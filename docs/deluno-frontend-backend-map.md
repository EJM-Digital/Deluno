# Deluno Frontend And Backend Map

Updated: 2026-05-13

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
- manage monitoring and metadata state
- search for missing or wanted movies
- review upgrades
- resolve imports and failures
- run bulk actions

Current subviews and route direction:

- `Movies / Library`
- `Movies / Wanted`
- `Movies / Import Review`
- `Movies / Upgrades` remains conceptually important even where the backend still folds that state into broader wanted/library flows

### TV Shows Area

Primary jobs:

- understand TV library health
- manage show, season, and episode monitoring
- search for missing episodes and shows
- review upgrades
- resolve imports and failures
- run bulk actions

Current subviews and route direction:

- `TV Shows / Library`
- `TV Shows / Wanted`
- `TV Shows / Import Review`
- `TV Shows / Upgrades` remains conceptually important even where the backend still folds that state into broader wanted/library flows

### Indexers Area

Primary jobs:

- add and test indexers
- manage download clients
- map services to libraries
- inspect source and client health
- run federated search support flows
- inspect normalized telemetry and queue actions

Current subviews and route direction:

- `Indexers / Sources`
- `Indexers / Download Clients`
- `Indexers / Search`
- `Indexers / Health`

### Activity Area

Primary jobs:

- see what Deluno did
- understand what is pending
- find attention items quickly
- observe queue/import/health changes

Current subviews and route direction:

- `Activity / Timeline`
- `Activity / Queue`
- `Activity / Attention`

## Backend Ownership Boundaries

### Deluno.Platform

Owns:

- authentication and bootstrap
- app settings
- libraries and routing
- quality profiles, tags, custom formats, intake sources, destination rules, and policy sets
- migration flows
- API keys
- system health/log/job views
- the expanding operations layer around analytics, cleanup, explanations, observability, presets, resilience, and settings

### Deluno.Movies

Owns:

- movie catalog
- movie monitoring
- wanted state
- search and grab flows
- metadata refresh/linking/job requests
- movie import recovery
- movie bulk actions

### Deluno.Series

Owns:

- series catalog
- show, season, and episode monitoring
- wanted state
- inventory views
- search and grab flows
- metadata refresh/linking/job requests
- series import recovery
- series bulk actions

### Deluno.Integrations

Owns:

- indexers
- download clients
- source routing normalization
- connection tests
- health snapshots
- download telemetry
- queue actions and grabs
- webhook ingestion
- metadata provider seams
- search adapter seams

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

### Deluno.Realtime

Owns:

- SignalR hub wiring
- live event delivery for queue, activity, health, search, import, and automation surfaces

## In-Flight Supporting Seams

The working tree contains additional design-forward seams:

- `Deluno.Library`
- `Deluno.Search`

These currently read more like future extraction points than settled production modules. Keep them documented, but do not assume they replace the stable ownership map yet.

## Cross-Cutting Engines

### Search And Routing

Lives mostly in:

- `Deluno.Integrations`
- `Deluno.Platform`
- movie and series route handlers that apply library-aware context

### Wanted, Upgrade, And Recovery Workflows

Lives mostly in:

- `Deluno.Movies`
- `Deluno.Series`
- `Deluno.Platform` for shared policy and library configuration

### Activity And Visibility

Lives mostly in:

- `Deluno.Jobs`
- `Deluno.Realtime`
- `Deluno.Platform` operational endpoints

### Refine-Before-Import

Lives mostly in:

- `Deluno.Platform`
- `Deluno.Filesystem`
- `Deluno.Integrations` processor and client coordination seams

## Current Direction

The codebase is moving toward:

- operational media workspaces rather than thin catalog pages
- indexers replacing generic connections as the user-facing mental model
- richer library-aware routing, health, and explanation surfaces
- more live revalidation via SignalR
- broader platform orchestration without collapsing movie and TV boundaries
