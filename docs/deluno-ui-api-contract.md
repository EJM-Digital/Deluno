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
- `GET /api/movies/page` — the paged catalogue list; see below
- `POST /api/movies/{id}/metadata/jobs`
- `POST /api/movies/metadata/jobs` — `{ forceAll?, take? }` → `{ enqueuedCount, remainingCount, staleCount, markedForRefreshCount, message }`
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
- `GET /api/series/page` — the paged catalogue list; see below
- `POST /api/series/{id}/metadata/jobs`
- `POST /api/series/metadata/jobs` — same shape as the movie twin
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

- launch provider chain is broker -> TMDb direct fallback -> stale cache; legacy
  OMDb support is retained only for migration compatibility and is not part of
  the managed launch service
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
- platform settings now include `searchScoringMode` (`hybrid`, `rules-only`, `ml-only`) so users can explicitly choose deterministic ranking, model-priority ranking, or both
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
- `GET /api/intelligent-routing/snapshot`
- `GET /api/intelligent-routing/anomalies`
- `POST /api/intelligent-routing/recommend-release`

Current UI contract expectations:

- release explain responses include deterministic decision details plus ML model probability/boost explanation when enabled
- release explain responses now include `ruleScore`, resolved `scoringMode`, and `scoringExplanation` so users can see how final score composition was chosen
- ranking model status exposes active model version, last training metadata, sample count, and evaluation metrics
- training and rollback controls are operational endpoints for guarded model lifecycle actions
- intelligent-routing snapshot exposes learned quality/release-group preferences and success-rate maps for indexers and download clients
- runtime download-client selection can now consider historical success rates in addition to static priority

Current gaps:

- deeper routing experimentation (bandits, longer-horizon feedback loops) remains future work; hard policy safety remains deterministic

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
- `POST /api/libraries/{id}/import-existing` — starts a run, returns `202` with its progress
- `GET /api/libraries/{id}/import-existing` — the run in flight, or the most recent one
- `POST /api/libraries/{id}/import-existing/pause`
- `POST /api/libraries/{id}/import-existing/resume`
- `POST /api/libraries/{id}/import-existing/cancel`
- `GET /api/libraries/{id}/import-existing/issues?take=100` — what the run set aside for review
- `GET /api/libraries/{id}/routing`
- `PUT /api/libraries/{id}/routing`

Current UI contract expectations:

- library automation, workflow, quality-profile assignment, and routing are already implemented settings surfaces
- routing is library-aware and should remain the place where indexer/download-client normalization is consumed by higher-level workflows

Importing an existing library is a **tracked background operation**, not a request
that returns when the work is done. `POST .../import-existing` creates (or re-attaches
to) a run and returns immediately with `202`; a worker advances it in slices. This is a
breaking change from the previous shape, which returned `200` with a final counts object
after doing the whole import inline — at 20,000 items that request ran for hours and
timed out.

The progress body is `{ run, percentComplete, itemsPerSecond, estimatedSecondsRemaining }`.
`run.status` is one of `queued`, `running`, `paused`, `completed`, `cancelled`, `failed`.
There is at most one active run per library, so a second POST returns the existing one.
Poll the `GET` for progress; it reads a single row and costs the same at 20 items or
200,000.

Current gaps:

- there is no dedicated update-library endpoint yet beyond the specific sub-settings routes
- routing preview/explanation payloads can still get richer

The metadata refresh endpoints select candidates in SQL and return **honest counts**:
`enqueuedCount` is the batch primed now, `remainingCount` is what is still to go, and
`message` phrases both for display. `forceAll` marks the whole library as wanting a
refresh in one statement rather than queueing a page of jobs — the backfill then works
through it continuously. This is a breaking change: the response no longer carries a
`jobs` array, which on a large library was up to 1,000 job objects and reported a few
percent of the work as if it were all of it.

### The paged catalogue list

`GET /api/{movies,series}/page` takes `search`, `status`, `sort`, `direction`,
`pageSize` and `pageToken`, and returns
`{ items, nextPageToken, totalCount, facets }`.

- **Search, filter, sort and the counts all happen in SQL.** The list surface must
  not fetch the catalogue and work it out in the browser; that is what stops
  working as a library grows.
- **`pageToken` is keyset, not offset.** Page 400 costs what page 1 costs, and a row
  inserted while somebody scrolls cannot shift the window. An unreadable token means
  "start again", never an error — tokens travel in URLs and outlive deploys.
- **`totalCount` and `facets` are present on the first page only** (`null` on a
  continuation). Counting is the one part of the request that scans, so it happens
  once per filter rather than once per page.
- `sort` is one of `added` (default), `title`, `year`, `rating`; each has an index
  behind it. `status` is one of `all`, `monitored`, `unmonitored`, `downloaded`,
  `missing`, or `upgrades`. `pageSize` is clamped to 200.
- Rows carry `fileSizeBytes` and `currentQuality`, plus file path, codecs, audio
  details, release group, runtime, popularity, votes, and derived bitrate. The legacy
  unbounded catalogue routes were removed; consumers must use this paged contract.

### Outbound pacing

`GET /api/integrations/outbound-throttle` reports what Deluno is holding back and for
how long: `{ hosts: [{ host, waiting, grantedCount, refusedCount, totalWaitedSeconds,
nextPermitInSeconds }] }`.

Requests to indexers and metadata providers are paced **before** they are sent —
one request every two seconds per indexer host (the floor Prowlarr enforces), and a
ten-per-second budget for metadata providers. A request that cannot get a slot inside
the caller's budget is skipped and logged rather than queued indefinitely, because a
search job holding its lease past two minutes would be leased again and sent twice.

This endpoint exists so a throttle that is working can be told apart from a hang.

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
