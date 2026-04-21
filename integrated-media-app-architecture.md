# Integrated Media Automation App Architecture

## Working Name

Deluno

## Summary

This document defines a single integrated app that replaces the "two separate apps" experience of Radarr and Sonarr while preserving strict independence between movie and series workflows.

The core recommendation is:

- one product
- one login
- one UI shell
- one deployment target
- two isolated media engines

Movies and series should not share operational state, queues, profiles, import rules, or metadata records. They may share infrastructure and adapters, but they should behave like two self-contained systems living inside one app.

This approach gives users one place to manage their library without reintroducing the class of bugs where TV logic breaks movie logic, or a release/import rule from one side affects the other.

## Product Goals

- Provide a single app for movie and TV acquisition automation.
- Keep movie and series operations fully isolated.
- Make setup much simpler than running separate tools.
- Expose one coherent UI and permissions model.
- Support first-class external IDs, especially IMDb IDs.
- Allow multiple metadata providers behind a provider abstraction.
- Preserve advanced workflows for power users without making the default UX intimidating.

## Non-Goals

- A single merged queue where movie and episode jobs compete for the same business rules.
- A shared metadata model that flattens movies and episodic content into one operational type.
- Tight coupling to one metadata vendor.
- Trying to replace every adjacent tool on day one, such as subtitle managers or media server administration.

## Design Principles

### 1. One app, two domains

The app is unified at the product level, not at the business-logic level.

There are two bounded contexts:

- `Movies`
- `Series`

Each context owns its own:

- metadata cache
- profiles
- rules
- monitoring state
- search jobs
- grab history
- import history
- naming rules
- upgrade logic
- repair tools

### 2. Shared infrastructure only

Shared services are allowed only when they are generic platform capabilities:

- authentication
- users and roles
- notifications
- provider credential storage
- download client adapter framework
- indexer adapter framework
- background job runtime
- logging, metrics, tracing
- UI shell and navigation

### 3. Explicit boundaries over convenience

No movie component should query series tables directly. No series component should reuse movie import logic. Shared code should exist only in infrastructure or in carefully versioned common libraries.

### 4. External IDs are first-class citizens

The app should store and expose multiple external IDs per title:

- IMDb
- TMDb
- TVDb
- Trakt
- TVMaze
- internal canonical ID

IMDb should be the primary cross-provider identity visible to users where available, but not the sole metadata authority for TV.

## Why IMDb Should Not Be the Only TV Metadata Backbone

IMDb is extremely valuable, but it should not be the only source of truth for series automation.

Reasons:

- episodic workflows need rich season and episode structure
- TV automation needs alternate ordering support, specials handling, daily show handling, and edge-case metadata
- IMDb licensing and access are more restrictive than community app builders usually expect
- TV-focused providers are typically better aligned to the structure needed for monitoring and importing episodes

Recommended provider strategy:

- treat IMDb as a first-class identifier and enrichment source
- use a TV-oriented provider for series/season/episode operational metadata
- abstract providers so the app can support multiple backends per media type

Suggested defaults:

- Movies:
  `Primary metadata`: TMDb or IMDb-licensed data
  `Identity/enrichment`: IMDb
- Series:
  `Primary metadata`: TVDb or another TV-native provider
  `Identity/enrichment`: IMDb

## Product Architecture

## Top-Level Components

1. Web App
2. API Gateway
3. Movie Domain Service
4. Series Domain Service
5. Shared Integration Services
6. Worker Runtime
7. Database
8. Search Index
9. Object/File Storage for cached artwork and provider payload snapshots

## Recommended Deployment Model

Start with a modular monolith, not distributed microservices.

That means:

- one deployable backend
- one logical application
- one worker process type
- clear internal module boundaries
- separate queues and data stores per domain

Why:

- faster to build
- easier to debug
- lower ops overhead
- still allows strict domain isolation if the codebase is designed well

The modules can be split into separate services later only if scale or team structure demands it.

## Backend Module Layout

Recommended backend modules:

- `platform-auth`
- `platform-users`
- `platform-notifications`
- `platform-jobs`
- `platform-audit`
- `platform-config`
- `integrations-indexers`
- `integrations-download-clients`
- `integrations-metadata`
- `movies-catalog`
- `movies-monitoring`
- `movies-search`
- `movies-grabs`
- `movies-import`
- `movies-history`
- `series-catalog`
- `series-monitoring`
- `series-search`
- `series-grabs`
- `series-import`
- `series-history`
- `shared-filesystem`
- `shared-naming`

Important note:
`shared-naming` and `shared-filesystem` must remain utility-only. They cannot contain movie or series business rules.

## Domain Boundaries

## Movie Domain

Owns:

- movie entities
- editions
- release groups
- quality profiles
- custom format matching for movies
- monitoring flags
- search and auto-search policies
- grab decisions
- import decisions
- rename and upgrade decisions
- movie-specific repair actions

Does not know about:

- seasons
- episodes
- anime absolute numbering
- season packs
- episode daily numbering

## Series Domain

Owns:

- series entities
- season entities
- episode entities
- alternate episode orderings
- specials handling
- anime absolute numbering
- daily show support
- season pack logic
- episode upgrade policies
- series-specific repair actions

Does not know about:

- movie editions
- theatrical vs extended edition semantics
- collection-only movie logic

## Shared Platform

Owns no content semantics.

It only provides:

- credentials
- connectors
- scheduling runtime
- observability
- API composition
- UI composition

## Job and Queue Model

The app should never use one generic work queue for all media operations.

Use separate logical queues:

- `movies.metadata.refresh`
- `movies.search.auto`
- `movies.search.manual`
- `movies.download.poll`
- `movies.import.scan`
- `movies.import.execute`
- `movies.rename`
- `series.metadata.refresh`
- `series.search.auto`
- `series.search.manual`
- `series.download.poll`
- `series.import.scan`
- `series.import.execute`
- `series.rename`
- `platform.notifications`
- `platform.cleanup`

This prevents interference and allows different retry, priority, and rate-limit policies by domain.

## Data Model

Use SQLite as the only core database technology for Deluno.

For Windows and Docker home users, the best product is one that does not require a separate database server. Because SQLite allows many readers but only one writer per database file, Deluno should use multiple SQLite database files with strict ownership boundaries instead of one shared database.

## Database Layout

Use these database files:

- `platform.db`
- `movies.db`
- `series.db`
- `jobs.db`
- `cache.db`

This makes ownership explicit, reduces write contention, and reinforces the movie/series isolation model.

## Platform Tables

Store these in `platform.db`:

- users
- roles
- user preferences
- API keys
- notifications
- audit log
- system settings
- indexer configs
- download client configs
- metadata provider configs
- webhooks

## Movie Tables

Store these in `movies.db`:

- items
- external IDs
- images
- monitoring state
- library files
- release candidates
- grab history
- import history
- quality profiles
- custom formats
- naming profiles
- tags
- item tag links
- manual review queue

## Series Tables

Store these in `series.db`:

- shows
- external IDs
- seasons
- episodes
- episode orderings
- images
- monitoring state
- episode files
- release candidates
- grab history
- import history
- quality profiles
- custom formats
- naming profiles
- tags
- show tag links
- manual review queue

## Job Tables

Store these in `jobs.db`:

- scheduled jobs
- job runs
- job leases
- job attempts
- dead letters
- worker heartbeats

## Identity Model

Every media record should have:

- internal UUID
- media type
- canonical title
- canonical year
- original title if different
- status
- provider provenance
- confidence score for provider merges

External IDs should be stored in separate tables, not embedded blobs, so cross-provider matching stays queryable and auditable.

## Metadata Provider Abstraction

Define one provider interface per domain, not one giant generic metadata provider API.

Recommended interfaces:

- `MovieMetadataProvider`
- `SeriesMetadataProvider`
- `ArtworkProvider`
- `RatingsProvider`

Example responsibilities:

### MovieMetadataProvider

- search by title/year
- get movie by external ID
- fetch images
- fetch credits
- fetch release dates
- fetch aliases and alternate titles

### SeriesMetadataProvider

- search series by title/year
- get show by external ID
- fetch seasons and episodes
- fetch alternate orderings
- fetch specials
- fetch images
- fetch aliases and air dates

### RatingsProvider

- fetch audience rating
- fetch vote counts
- fetch popularity signals

IMDb fits especially well as:

- ID source
- ratings source
- popularity source
- enrichment provider

It is less ideal as the only operational source for TV episode monitoring.

## Search and Grab Pipeline

The search pipeline should be structurally similar across domains but implemented separately.

Shared stages:

1. Select monitored targets
2. Build search intent
3. Query indexers
4. Normalize results
5. Score results
6. Apply rejection rules
7. Choose candidate
8. Send to download client
9. Record grab history

Domain-specific logic must diverge in stages 2, 5, and 6.

Movie search intent examples:

- title plus year
- edition hints
- language
- minimum quality
- preferred source

Series search intent examples:

- series title
- season/episode tuple
- air date
- absolute episode number
- season pack eligibility
- anime rules
- specials handling

## Import Pipeline

Imports are one of the hardest parts of the system and should be implemented as a multi-stage state machine.

Recommended import stages:

1. Detect completed download
2. Enumerate files
3. Parse filenames
4. Match against monitored targets
5. Resolve ambiguity
6. Apply upgrade rules
7. Execute import plan
8. Rename or hardlink according to policy
9. Emit history and notifications

Movie and series importers should share parser utilities, but not decision logic.

### Movie Import Concerns

- edition collisions
- one title per directory assumptions
- remux vs encode preference
- multi-language tracks
- upgrade replacement logic

### Series Import Concerns

- season packs
- multi-episode files
- daily episodes
- anime numbering
- specials
- alternate ordering systems
- duplicate episode mappings

## Filesystem and Storage Strategy

Support these import modes:

- hardlink
- copy
- move

Support these library layouts:

- separate movie and TV roots
- multiple roots per domain
- optional tenant/user-specific roots later

Recommended constraints:

- movie roots and series roots are configured independently
- import rules can never target a root owned by the other domain unless explicitly allowed
- every import action records source path, destination path, and operation type

## UI Architecture

The UI should feel like one product with two workspaces.

Recommended top nav:

- Home
- Discover
- Movies
- Series
- Activity
- Calendar
- Wanted
- History
- Settings

Key UX principle:
users should switch contexts intentionally. The UI should not pretend movies and TV are identical.

## Shared Screens

- dashboard/home
- activity queue
- notifications
- settings
- search add flow shell
- global health checks

## Movie-Only Screens

- movie details
- movie manual search
- movie files
- movie editions
- movie quality profile management

## Series-Only Screens

- show details
- season and episode table
- episode monitoring controls
- season pack/manual search
- alternate ordering views
- anime and daily-series controls

## Discover Experience

The "Add" flow should be one of the biggest UX improvements over separate apps.

Recommended flow:

1. User searches once.
2. Results are grouped by media type.
3. The app displays IMDb ID and other provider IDs when helpful.
4. User chooses `Movie` or `Series`.
5. The domain-specific add form appears with only relevant controls.

This keeps the app unified without merging the operational models.

## API Design

Use a clean HTTP API first. GraphQL can be added later if the frontend truly benefits.

Recommended route families:

- `/api/platform/*`
- `/api/movies/*`
- `/api/series/*`

Examples:

- `POST /api/movies/search`
- `POST /api/series/search`
- `POST /api/movies/{id}/monitor`
- `POST /api/series/{id}/episodes/{episodeId}/monitor`
- `POST /api/movies/{id}/manual-search`
- `POST /api/series/{id}/manual-search`
- `POST /api/movies/import/review/{id}/approve`
- `POST /api/series/import/review/{id}/approve`

Do not create endpoints that return mixed operational records unless the use case is platform-level, such as activity or global search.

## Permissions Model

At minimum support:

- admin
- manager
- read-only

Later extensions:

- domain-scoped permissions for movies-only or series-only operators
- action-level permissions for import approval, delete, and config changes

## Observability and Recovery

This app will only feel trustworthy if it is extremely transparent about why it made decisions.

Build in:

- structured logs per domain
- job tracing
- decision explanations for grabs and rejections
- import plan previews
- per-item history timelines
- health checks for each provider
- connector latency and error dashboards

Every automated action should be explainable in the UI.

## Recommended Tech Stack

One strong implementation path:

- Backend: ASP.NET Core 10
- API: ASP.NET Core HTTP API
- Storage: SQLite with multiple DB files
- Data access: Dapper for hot paths, limited EF Core where helpful
- Realtime: SignalR
- Workers: same codebase, separate worker process mode if needed
- Frontend: React with TypeScript
- Search: SQLite indexes and application-level composition first, dedicated search engine only if the real product demands it later
- Packaging: self-contained Windows build and single-container Docker deployment

Why this stack:

- strong concurrency and filesystem tooling on the backend
- excellent Windows packaging story
- simple Docker story for home users
- no required companion services
- good typed contracts
- straightforward ops for self-hosting

## MVP Scope

A realistic MVP should avoid trying to match mature Radarr and Sonarr feature-for-feature.

## MVP Features

- single app shell with movies and series workspaces
- separate movie and series catalogs
- provider abstraction with one movie-capable provider and one series-capable provider
- IMDb ID support everywhere records are shown
- indexer integration
- one torrent client and one Usenet client integration
- monitored search
- basic grab logic
- basic completed-download import
- hardlink/copy/move import options
- separate quality profiles for movies and series
- activity/history views
- notification hooks

## MVP Exclusions

- advanced anime edge cases
- alternate orderings beyond basic support
- multi-user tenancy
- plugin marketplace
- subtitle automation
- recommendation/discovery engine
- advanced repair tooling
- mobile app

## V1 Features

- robust manual import and conflict resolution
- season packs and multi-episode handling
- richer release scoring
- advanced custom formats
- alternate orderings and specials support
- migration tools from Radarr and Sonarr
- richer dashboard and wanted views
- stronger audit and explanation tooling
- broader provider support

## Migration Strategy

Migration is strategically important if this app wants real adoption.

Support importers for:

- movie library roots
- series library roots
- quality profiles
- naming profiles
- tags
- monitored items
- history where practical
- external IDs

Migration should never auto-merge movie and series settings. Preserve domain boundaries even when importing.

## Biggest Risks

1. Import correctness
2. TV edge-case coverage
3. Metadata provider licensing and costs
4. False-positive grabs
5. Over-sharing of code between domains
6. Building a "unified" UX that actually just hides complexity poorly

## Engineering Guardrails

To avoid drift toward a tangled architecture:

- separate backend modules and database schemas by domain
- domain-owned tests for movie and series workflows
- no shared domain entities
- no cross-domain foreign keys
- separate queues and job handlers
- explicit API namespaces
- architecture tests that block forbidden module imports

## Suggested Delivery Plan

## Phase 1: Foundation

- scaffold backend modules and schemas
- build auth, settings, job runtime, connector framework
- implement metadata provider abstractions
- create shared UI shell

## Phase 2: Movie Vertical Slice

- movie search/add
- monitor and auto-search
- grab pipeline
- import pipeline
- history and manual review

## Phase 3: Series Vertical Slice

- series search/add
- episode monitoring
- auto-search
- season and episode import
- history and manual review

## Phase 4: Unified UX and Recovery

- global dashboard
- activity and wanted views
- health checks
- conflict resolution
- import explanations

## Phase 5: Migration and Hardening

- Radarr/Sonarr importers
- better edge-case coverage
- observability improvements
- performance tuning

## Team and Timeline Estimate

For a serious self-hosted v1:

- 2 strong engineers for 6 to 9 months for an MVP with clear limitations
- 3 to 5 engineers for 12 to 18 months for a polished v1

If the goal is "better than both existing tools" rather than "good enough unified alternative," plan for a multi-year effort due to import correctness, TV edge cases, and ecosystem compatibility.

## Final Recommendation

Build one app with two autonomous media engines.

Do not merge movie and series business logic.

Use IMDb as:

- a first-class external ID
- a ratings and popularity source
- an enrichment source when licensing allows

Do not make IMDb the only TV operational metadata source.

The product win is not that movies and TV share a code path. The product win is that users only have to learn, deploy, and manage one app while the system quietly preserves the separation needed for reliability.
