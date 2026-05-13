# Deluno Repo Change History

Updated: 2026-05-13

## Purpose

This document answers the practical repo-forensics question:

"What has changed in this checkout since the last clearly shared baseline?"

Because agent memory is not persistent across sessions, this file uses Git as the source of truth.

## Baseline Used For This Pass

Shared merge-base between local `main` and `origin/main`:

- `0d9938a` `Introduce versioned media policy engine`
- author date: 2026-04-29

At the time of this pass, local `main` was:

- ahead of `origin/main` by 6 commits
- behind `origin/main` by 7 commits
- carrying a large uncommitted working-tree delta on top

That means the repo state has three distinct layers:

1. local committed work not on remote `main`
2. remote committed work not in this local branch
3. uncommitted in-flight work in the checkout itself

## Commit-By-Commit: Local `main` Since The Baseline

### `7914c5e` Surface download client telemetry capabilities

What changed:

- added backend models and services for normalized download-client telemetry
- exposed telemetry to the web app
- started treating client capabilities as part of the integration contract

Primary impact:

- queue, dashboard, and indexer UI flows gained awareness of client state instead of using thinner static configuration alone

### `2588466` Normalize download telemetry status handling

What changed:

- introduced shared frontend telemetry helpers
- normalized status translation logic
- reduced duplicated status interpretation across routes
- added telemetry profile tests

Primary impact:

- download-client queue state became more consistent across Dashboard, Queue, and Indexer surfaces

### `04967d5` Add agent-first repository scaffolding

What changed:

- added `AGENTS.md`
- added `docs/README.md`, `docs/ARCHITECTURE.md`, and `docs/QUALITY_SCORE.md`
- added agent-readiness validation and execution-plan structure

Primary impact:

- repository context moved into versioned docs and validation instead of relying on thread memory

### `48a83ce` Movies/TV workspace sub-routes

What changed:

- added workspace sub-routes and shells for Movies/TV
- added dedicated wanted/import-oriented route surfaces

Primary impact:

- product navigation shifted from a flatter shell toward operational media workspaces

### `f6e87c0` Indexer/client enable-disable toggle and update endpoints

What changed:

- added `PUT` update endpoints for indexers and download clients
- enabled explicit enable/disable behavior in platform settings flows

Primary impact:

- integrations moved from create/delete/test-only management toward true editable lifecycle support

### `29aea01` Custom format matching engine with dry-run panel

What changed:

- added `CustomFormatMatcher`
- added custom-format dry-run endpoint
- expanded the settings UI for multi-condition custom-format authoring

Primary impact:

- custom formats became an inspectable decision surface instead of a mostly static configuration artifact

## Commit-By-Commit: `origin/main` Since The Same Baseline

These commits exist on remote `main` but are not present in the local branch inspected during this pass:

- `f98ea8d` episode-level TV workflows
- `845b88e` movie replacement protection, quality scoring, and UI language improvements
- `c072239` CI cleanup removing broken AI-generated e2e tests
- `b3d8137` Windows tray app, installer, and CI hardening
- `c940703` installer packaging, icon, ffprobe bundling, and CI hardening
- `07ca062` release-blocker fixes for `v0.1.0`
- `98272a4` tray build and Docker restore fixes

Primary implication:

- this checkout is not a simple "latest main plus local edits" state
- it is a divergent branch snapshot with both missing upstream work and unique local work

## Subsystem-By-Subsystem: Current Working Tree Expansion

The uncommitted delta is much larger than the six local commits and changes the effective product shape of the checkout.

### Platform

New or expanded platform areas include:

- analytics
- cleanup policy
- decisions
- explanations
- idempotency
- import recovery
- import lists
- multi-library coordination
- monitoring
- observability
- operations
- presets
- realtime coordination
- resilience
- settings
- quality protection
- system endpoints

Representative paths:

- `src/Deluno.Platform/Analytics`
- `src/Deluno.Platform/Cleanup`
- `src/Deluno.Platform/Decisions`
- `src/Deluno.Platform/Explanations`
- `src/Deluno.Platform/Idempotency`
- `src/Deluno.Platform/Import`
- `src/Deluno.Platform/ImportLists`
- `src/Deluno.Platform/Libraries`
- `src/Deluno.Platform/Monitoring`
- `src/Deluno.Platform/Observability`
- `src/Deluno.Platform/Operations`
- `src/Deluno.Platform/Presets`
- `src/Deluno.Platform/Quality`
- `src/Deluno.Platform/RealTime`
- `src/Deluno.Platform/Resilience`
- `src/Deluno.Platform/Settings`
- `src/Deluno.Platform/SystemEndpointRouteBuilderExtensions.cs`

Meaning:

- Deluno is moving from a thinner settings-and-routing platform module toward a broader app-services layer for orchestration, observability, and guided operations

### Integrations

New or expanded integration work includes:

- queue actions against download clients
- direct release grabs
- public webhook ingestion for qBittorrent and SABnzbd
- generic completion/failure webhook handling
- telemetry persistence store
- download grab history concepts
- metadata fallback services
- explicit indexer integration service abstractions
- richer scoring breakdowns

Representative paths:

- `src/Deluno.Integrations/DownloadClients/DownloadClientEndpointRouteBuilderExtensions.cs`
- `src/Deluno.Integrations/DownloadClients/SqliteDownloadClientTelemetryStore.cs`
- `src/Deluno.Integrations/DownloadClients/DownloadClientWebhookService.cs`
- `src/Deluno.Integrations/DownloadClients/IntelligentClientRouter.cs`
- `src/Deluno.Integrations/Metadata/MetadataFallbackService.cs`
- `src/Deluno.Integrations/Search/DefaultIndexerIntegrationService.cs`
- `src/Deluno.Integrations/Search/SearchResultScoringBreakdown.cs`

Meaning:

- Deluno is increasingly behaving as an orchestrator of external indexers and clients rather than a static configuration layer

### Movies And Series

The movie and series endpoint surfaces have grown into broader operational APIs.

Movies now include:

- import recovery management
- wanted views
- search history
- monitoring updates
- metadata refresh and linking
- metadata job scheduling
- bulk quality-profile, tags, and search actions

Series now include:

- import recovery management
- wanted views
- series and per-series inventory
- search history
- series monitoring updates
- episode monitoring updates
- episode and season search flows
- metadata refresh and linking
- metadata job scheduling
- bulk quality-profile, tags, and search actions

Meaning:

- the separate movie and TV engines are still preserved, but both are evolving from catalog endpoints into fuller workflow owners

### Realtime

Realtime contracts now cover more than the original basic health/download cases.

Implemented or modeled event areas include:

- health changes
- download progress
- normalized telemetry changes
- activity additions
- queue item add/remove
- search progress
- import status
- automation status

Relevant paths:

- `src/Deluno.Realtime/IRealtimeEventPublisher.cs`
- `src/Deluno.Realtime/SignalRRealtimeEventPublisher.cs`
- `apps/web/src/lib/use-signalr.tsx`

Meaning:

- the product is moving toward live operational surfaces, but the event contract still has drift between ambition, interface shape, and frontend usage

### Frontend

The web app now has a substantially richer operational shell.

Notable in-flight UI changes include:

- more advanced library view controls and saved filters
- activity filtering
- media bulk action toolbar
- expanded route refresh behavior tied to SignalR
- additional shared UI primitives such as `select`
- new e2e coverage under `apps/web/tests/e2e`

Representative paths:

- `apps/web/src/components/app/library-view.tsx`
- `apps/web/src/components/app/activity-filters.tsx`
- `apps/web/src/components/app/media-bulk-action-toolbar.tsx`
- `apps/web/src/lib/use-signalr.tsx`

Meaning:

- the frontend is no longer just catching up to backend surfaces; it is now defining richer interaction requirements that the API contract should explicitly support

### Documentation

Documentation growth is broad:

- analytics
- API
- deployment
- monitoring
- observability
- intelligent routing
- quality scoring
- realtime events
- troubleshooting
- user guide
- many other subsystem docs

Important structural change:

- `docs/exec-plans/active/agent-first-realignment.md` was moved out of `active`
- the completed version now lives at `docs/exec-plans/completed/agent-first-realignment.md`
- `docs/exec-plans/active/` currently contains only `.gitkeep`

Meaning:

- documentation coverage is wider, but the highest-trust docs were lagging the code until this repair pass

## Current Reading Of Product Direction

As of this pass, the repo is converging on:

- a single-user Deluno application
- strict internal separation between movie and TV engines
- Deluno as the orchestrator of indexers, download clients, import processors, and metadata providers
- library-aware routing, policy, and quality controls as first-class product features
- a more operational UI centered on wanted, queue, import recovery, health, and bulk workflows

## Documentation Implications

The highest-value docs should always answer three questions clearly:

1. what is implemented now
2. what is in-flight in this checkout
3. what is still only intent or roadmap

During this pass, the main repair targets were:

- `docs/README.md`
- `docs/ARCHITECTURE.md`
- `docs/QUALITY_SCORE.md`
- `docs/deluno-capability-map.md`
- `docs/deluno-frontend-backend-map.md`
- `docs/deluno-ui-api-contract.md`

If future work materially changes the branch-divergence story or introduces another large in-flight subsystem, update this document in the same change.
