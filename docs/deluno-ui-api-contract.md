# Deluno UI API Contract

Updated: 2026-05-14

## Purpose

This document is the frontend-facing contract summary for the current Deluno API surface.

It distinguishes between:

- implemented endpoints that the web app can use now
- implemented but still thin endpoints that need richer payloads later
- gaps that are still roadmap items

This file should stay aligned with route registration in:

- `src/Deluno.Platform/PlatformEndpointRouteBuilderExtensions.cs`
- `src/Deluno.Movies/MoviesEndpointRouteBuilderExtensions.cs`
- `src/Deluno.Series/SeriesEndpointRouteBuilderExtensions.cs`
- `src/Deluno.Integrations/DownloadClients/DownloadClientEndpointRouteBuilderExtensions.cs`
- `src/Deluno.Integrations/Search/SearchEndpointRouteBuilderExtensions.cs`
- `src/Deluno.Platform/SystemEndpointRouteBuilderExtensions.cs`

## Authentication And Session

Implemented:

- `POST /api/auth/login`
- `GET /api/auth/bootstrap-status`
- `POST /api/auth/bootstrap`
- `POST /api/auth/logout`
- `PUT /api/auth/password`

UI expectations:

- bootstrap and login stay in the platform surface, not in host-only glue
- authenticated flows should assume single-user behavior, not multi-tenant admin workflows

## System And Realtime

Implemented REST endpoints:

- `GET /api/system/health`
- `GET /api/system/logs`
- `GET /api/system/jobs`
- `GET /api/monitoring/dashboard`
- `GET /api/monitoring/alerts`
- `GET /api/monitoring/diagnostics?query=&category=&severity=&sinceUtc=&take=`
- `GET /api/monitoring/export/prometheus`
- `GET /api/monitoring/export/influx`
- `GET /api/openapi/v1.json`
- `GET /api/docs`

Implemented SignalR hub:

- `/hubs/deluno`

Implemented SignalR event names currently modeled in frontend/backend contracts:

- `DownloadProgress`
- `DownloadTelemetryChanged`
- `QueueItemAdded`
- `QueueItemRemoved`
- `HealthChanged`
- `ActivityEventAdded`
- `SearchProgress`
- `ImportStatus`
- `AutomationStatus`

Current gap:

- the backend publisher contains broader event ambitions than the shared interface and current frontend subscriptions fully model
- import/recovery and wanted-state coverage is still incomplete and should not be assumed to be authoritative everywhere
- monitoring export endpoints are intentionally unauthenticated only inside Deluno's authenticated API boundary (no public scrape endpoint)

## Movies

Implemented endpoints:

- `GET /api/movies`
- `GET /api/movies/import-recovery`
- `GET /api/movies/wanted`
- `GET /api/movies/search-history`
- `POST /api/movies/import-recovery`
- `DELETE /api/movies/import-recovery/{id}`
- `GET /api/movies/{id}`
- `PUT /api/movies/monitoring`
- `POST /api/movies/{id}/search`
- `POST /api/movies/{id}/grab`
- `POST /api/movies/{id}/metadata/refresh`
- `POST /api/movies/{id}/metadata/link`
- `PUT /api/movies/{id}/metadata/override`
- `POST /api/movies/{id}/metadata/jobs`
- `POST /api/movies/metadata/jobs`
- `POST /api/movies`
- `POST /api/movies/bulk`
- `DELETE /api/movies/bulk`
- `POST /api/movies/bulk/quality-profile`
- `POST /api/movies/bulk/tags`
- `POST /api/movies/bulk/search`
- `POST /api/movies/bulk/reassign-library`
- `POST /api/movies/bulk/rename-preview`
- `GET /api/movies/duplicates`

Current UI contract expectations:

- library rows should treat `GET /api/movies` as the source of truth for catalog state
- wanted and import-recovery are first-class operational views, not hidden utilities
- monitoring, search, metadata, and bulk actions are already part of the live product surface

Current gaps:

- no paging, filtering, or sorting query contract is exposed at the route layer yet
- the library view UI is outgrowing the current list payload shape and will need richer filtering/summary contracts

## Series

Implemented endpoints:

- `GET /api/series`
- `GET /api/series/import-recovery`
- `GET /api/series/wanted`
- `GET /api/series/inventory`
- `GET /api/series/{id}/inventory`
- `GET /api/series/search-history`
- `POST /api/series/import-recovery`
- `DELETE /api/series/import-recovery/{id}`
- `GET /api/series/{id}`
- `PUT /api/series/monitoring`
- `PUT /api/series/episodes/monitoring`
- `POST /api/series/{id}/search`
- `POST /api/series/{id}/metadata/refresh`
- `POST /api/series/{id}/metadata/link`
- `PUT /api/series/{id}/metadata/override`
- `POST /api/series/{id}/metadata/jobs`
- `POST /api/series/metadata/jobs`
- `POST /api/series/{id}/episodes/search`
- `POST /api/series/{id}/grab`
- `POST /api/series/{id}/seasons/{seasonNumber}/search`
- `POST /api/series`
- `POST /api/series/bulk`
- `DELETE /api/series/bulk`
- `POST /api/series/bulk/quality-profile`
- `POST /api/series/bulk/tags`
- `POST /api/series/bulk/search`
- `POST /api/series/bulk/reassign-library`
- `POST /api/series/bulk/rename-preview`

Current UI contract expectations:

- series routes already support episode-level monitoring and episode/season search initiation
- inventory endpoints are the current bridge between a series shell and episode-aware workflows
- wanted/import-recovery/search-history should be treated as operational views, not future placeholders

Current gaps:

- there is still no dedicated `GET /api/series/{id}/episodes` route; episode inventory currently carries that load
- richer pagination, season filtering, and dedicated upgrade-specific views remain roadmap work
- bulk rename preview is implemented, but rename-apply remains part of import/organizer workflows instead of an immediate endpoint

## Metadata Integrations

Implemented endpoints:

- `GET /api/metadata/status`
- `GET /api/metadata/search`
- `POST /api/metadata/test`
- `DELETE /api/metadata/cache`
- `GET /api/metadata/artwork/{cacheKey}`
- `GET /api/metadata/broker/status`
- `GET /api/metadata/broker/search`

Current behavior:

- provider fallback chain is broker -> TMDb direct -> OMDb fallback -> stale cache
- artwork URLs from provider responses are localized into Deluno's cached artwork route when downloadable
- metadata refresh jobs can be queued manually from movie/series routes, and maintenance automation schedules stale or missing metadata refreshes
- manual override routes allow users to patch metadata fields when provider payloads are incomplete

## Platform Settings And Configuration

Implemented endpoints:

- `GET /api/settings`
- `PUT /api/settings`
- `POST /api/setup/completed`

Implemented CRUD surfaces:

- `GET|POST|PUT|DELETE /api/quality-profiles`
- `PUT /api/quality-profiles/order`
- `GET|PUT /api/quality-model`
- `GET|POST|PUT|DELETE /api/tags`
- `GET|POST|PUT|DELETE /api/intake-sources`
- `POST /api/intake-sources/{id}/sync`
- `GET /api/intake-sources/{id}/diagnostics`
- `GET|POST|PUT|DELETE /api/custom-formats`
- `POST /api/custom-formats/dry-run`
- `GET|POST|PUT|DELETE /api/destination-rules`
- `POST /api/destination-rules/resolve`
- `GET|POST|PUT|DELETE /api/policy-sets`
- `GET|POST|PUT|DELETE /api/library-views`
- `POST /api/migration/preview`
- `POST /api/migration/apply`

Current UI contract expectations:

- quality profiles, tags, intake sources, custom formats, destination rules, policy sets, and saved library views are all active configuration concepts
- intake sources now carry per-source filter/routing fields (`requiredGenres`, `minimumRating`, `minimumYear`, `maximumAgeDays`, `allowedCertifications`, `audience`) plus sync diagnostics (`lastSyncUtc`, `lastSyncStatus`, `lastSyncSummary`)
- the quality model endpoint exposes explicit editable tiers with movie/episode size bounds and upgrade-stop policy
- custom format dry-run is implemented and should be documented as a real workflow, not a future one
- migration preview/apply exists and should remain tied to authenticated single-user setup and import workflows

Current gaps:

- the platform route file is carrying too many responsibilities; contract clarity is now stronger than implementation separation
- several newer docs describe presets and advanced settings, but the route registration is still concentrated in a single large endpoint file

## Search Scoring And Explainability

Implemented endpoints:

- `POST /api/releases/explain`
- `GET /api/ranking-model/status`
- `POST /api/ranking-model/train`
- `POST /api/ranking-model/rollback`

Current UI contract expectations:

- release explain responses include deterministic decision details plus ML model probability/boost explanation when enabled
- ranking model status exposes active model version, last training metadata, sample count, and evaluation metrics
- training and rollback controls are operational endpoints for guarded model lifecycle actions

Current gaps:

- broader intelligent routing beyond bounded score boost remains roadmap work; hard policy safety remains deterministic

## Libraries And Routing

Implemented endpoints:

- `GET /api/libraries`
- `POST /api/libraries`
- `DELETE /api/libraries/{id}`
- `PUT /api/libraries/{id}/automation`
- `PUT /api/libraries/{id}/quality-profile`
- `PUT /api/libraries/{id}/workflow`
- `POST /api/libraries/{id}/search-now`
- `POST /api/libraries/{id}/skip-cycle`
- `POST /api/libraries/{id}/import-existing`
- `GET /api/libraries/{id}/routing`
- `PUT /api/libraries/{id}/routing`

Current UI contract expectations:

- library automation, workflow, quality-profile assignment, and routing are already implemented settings surfaces
- routing is library-aware and should remain the place where indexer/download-client normalization is consumed by higher-level workflows

Current gaps:

- there is no dedicated update-library endpoint yet beyond the specific sub-settings routes
- routing preview/explanation payloads can still get richer

## Indexers And Download Clients

Implemented indexer endpoints:

- `GET /api/indexers`
- `POST /api/indexers`
- `POST /api/indexers/test`
- `DELETE /api/indexers/{id}`
- `PUT /api/indexers/{id}`
- `POST /api/indexers/{id}/test`

Implemented download-client endpoints:

- `GET /api/download-clients`
- `POST /api/download-clients`
- `POST /api/download-clients/test`
- `DELETE /api/download-clients/{id}`
- `PUT /api/download-clients/{id}`
- `POST /api/download-clients/{id}/test`
- `GET /api/download-clients/telemetry`
- `GET /api/download-clients/telemetry/last-known`
- `POST /api/download-clients/{clientId}/queue/actions`
- `POST /api/download-clients/{clientId}/grab`

Implemented webhook endpoints:

- `POST /api/download-clients/{clientId}/webhook`
- `GET|POST|PUT|DELETE /api/notification-webhooks`
- `POST /api/notification-webhooks/{id}/test`

Current UI contract expectations:

- enable/disable and update flows are already implemented and should not be described as future-only
- telemetry has both live polling and persisted last-known behavior
- queue actions and manual direct grab are now part of the integration surface
- inbound download-client webhook payloads are normalized and idempotent across duplicate completed/failed callbacks
- outbound notification webhooks support event-filter matching, Discord embed formatting, and bounded retry delivery before recording final error state

Current gaps:

- reorder/prioritization contracts are still indirect
- deeper per-client history fidelity and outcome tracking remain incomplete

## Activity, Queue, And External Operations

Implemented core operational endpoints:

- `GET /api/jobs?take=n`
- `GET /api/activity?take=n`
- `GET /api/integrations/external/manifest`
- `GET /api/integrations/external/health`
- `GET /api/integrations/external/queue`
- `GET /api/integrations/external/activity`
- `POST /api/integrations/external/trigger-refresh`
- `POST /api/integrations/processors/events`

Current UI contract expectations:

- activity and queue are already fed by durable job/activity stores, not just transient UI state
- refine-before-import and external processor coordination are implemented platform concerns

Current gaps:

- queue and activity filtering/query richness remains limited
- import/recovery event streaming is still behind the product ambition

## API Keys

Implemented endpoints:

- `GET /api/api-keys`
- `POST /api/api-keys`
- `DELETE /api/api-keys/{id}`

Contract note:

- API keys are the supported external automation boundary; docs should prefer them over undocumented direct database access or UI scraping
