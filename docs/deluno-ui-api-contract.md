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
- `GET /api/monitoring/diagnostics?query=&category=&severity=&sinceUtc=&pageSize=&pageToken=`
- `GET /api/monitoring/export/prometheus`
- `GET /api/monitoring/export/influx`
- `GET /api/openapi/v1.json`
- `GET /api/docs`

Implemented SignalR hub:

- `/hubs/deluno`

Implemented SignalR event names currently modeled in frontend/backend contracts:

- `DownloadProgress`
- `QueueItemAdded`
- `QueueItemRemoved`
- `QueueItemStatusChanged`
- `HealthChanged`
- `ActivityEventAdded`
- `SearchRunCompleted`
- `ImportStateChanged`
- `DispatchGrabAttempt`
- `DispatchGrabCompleted`
- `DispatchDetected`
- `DispatchImportStarted`
- `DispatchImportCompleted`
- `MovieChanged`
- `SeriesChanged`
- `LibraryChanged`
- `SettingsChanged`
- `QualityProfileChanged`
- `PolicySetChanged`
- `IntakeSourceChanged`
- `AutomationStateChanged`
- `IndexerChanged`
- `DownloadClientChanged`

Current gap:

- entity-change events carry only `{ id }`; consumers refetch state rather than applying a second serialized copy
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
- `POST /api/movies/{id}/metadata/link/preview` — resolves the exact provider record and returns current/proposed identity, title/year/IMDb/collection changes, preserved local state, any held-title conflict, and a confirmation token bound to the current title state
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

- library rows should treat `GET /api/movies/page` as the source of truth for catalog state
- wanted and import-recovery are first-class operational views, not hidden utilities
- monitoring, search, metadata, and bulk actions are already part of the live product surface
- metadata remaps are two-step operations: the UI must show the preview and submit its confirmation token; a missing/stale token or a newly claimed identity is rejected without changing the title

Current gaps:

- the paged catalogue contract includes SQL-backed filtering, sorting, total count, and facets; callers must follow `nextPageToken` rather than request the whole catalogue

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
- `GET /api/series/{id}/numbering`
- `PUT /api/series/{id}/numbering` — selects standard/daily/anime and standard/air-date/absolute/scene keys; explicit owner mappings are protected from provider refreshes
- `PUT /api/series/monitoring`
- `PUT /api/series/episodes/monitoring`
- `POST /api/series/{id}/search`
- `POST /api/series/{id}/metadata/refresh`
- `POST /api/series/{id}/metadata/link/preview` — adds proposed season/episode counts and the number of existing episodes absent from the proposed provider catalogue
- `POST /api/series/{id}/metadata/link`
- `PUT /api/series/{id}/metadata/override`
- `GET /api/series/page` — the paged catalogue list; see below
- `POST /api/series/{id}/metadata/jobs`
- `POST /api/series/metadata/jobs` — same shape as the movie twin
- `POST /api/series/{id}/episodes/search` — each selected episode resolves its own installed path, size, current/target quality, and same-library immutable-plan evaluation before candidate comparison; replacement authority is bound to that exact path
- `POST /api/series/{id}/grab`
- `POST /api/series/{id}/seasons/{seasonNumber}/search` — one season-pack search; the query planner owns the season token and an imported `S01`/`Season 01` pack is expanded against the persisted catalogue inside one transaction. For a partly installed season, every installed episode must have an exact snapshot under the current immutable plan and the candidate must compare as a typed `Upgrade` against every snapshot. Missing/stale evidence or one lateral/worse comparison returns `held` and makes no dispatch. An accepted dispatch persists an episode→owned-path manifest; import revalidates it, backs up every distinct current file, places the complete pack, updates the catalogue atomically, and restores all old files/sources if the transaction fails.
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
- TV imports resolve standard multi-episode ranges (`S01E01-E03`), specials (`S00`), daily air dates, anime absolute numbers, scene numbers, and season packs without replacing the canonical provider episode identity; ambiguous alternate numbers remain unmatched for review
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
- provider status, broker status/search and metadata test results retain a typed
  failure when a provider returns an authentication, rate-limit, timeout,
  unavailable or malformed-response boundary. The UI should lead with its
  `message` and `nextAction`, while keeping provider/operation/status details
  available in the maintenance drawer.

Subtitle provider rows and test results follow the same typed failure contract.
When a provider fails and a later fallback succeeds, the successful subtitle or
metadata result remains successful while the earlier failure remains attached
to provider health/history for diagnosis. A failed attempt is never represented
as an unexplained empty search result.

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
- `GET /api/intake-sources/{id}/diagnostics?pageSize=n&pageToken=…`
- `GET|POST|PUT|DELETE /api/custom-formats`
- `POST /api/custom-formats/dry-run`
- `GET|POST|PUT|DELETE /api/destination-rules`
- `POST /api/destination-rules/resolve`
- `GET|POST|PUT|DELETE /api/policy-sets`
- `POST /api/policy-sets/{id}/effective-preview`
- `GET /api/media-plan-scenarios`
- `GET /api/media-plan-scenarios/{id}/compile?mediaType=movies|tv`
- `POST /api/media-plan-scenarios/{id}/apply`
- `GET|POST|PUT|DELETE /api/library-views`
- `POST /api/migration/preview`
- `POST /api/migration/apply`

Migration preview responses include operation-level provenance and warnings,
plus a secret-free `inventory` reconciliation. It reports input rows,
accounted rows, actions, and legacy classifications per source/category.
`unaccountedRowCount` is non-zero when an input row has no corresponding
operation, so callers should keep the preview in review rather than treating a
partial mapping as complete. The migration page can download this redacted
report before apply; apply persists the same inventory inside its audit report.

Media Plan scenarios are versioned, server-owned starting points. A dual-scope
scenario must be compiled with an explicit `mediaType`; the compiler returns
the existing policy-set request plus the readable consequences for size,
search, upgrades, subtitles, routing, sharing, cleanup, notifications, and
naming. Applying a scenario reuses an existing matching starter Quality Profile
when possible, creates the policy set through the normal policy-set repository,
and is idempotent for the generated scenario name/version. It does not create
a second release-ranking engine.

`POST /api/policy-sets/{id}/effective-preview` accepts optional field-level
`libraryOverride` and `titleOverride` objects plus the global automation gate.
It returns the resolved plan and one source record per field (`media-plan`,
`library`, `title`, or `global-safety`). The global gate is non-overridable and
can only pause a plan; this route is read-only and does not persist either
override.

Current UI contract expectations:

- TV remaps are blocked when another held show owns the proposed identity or when the proposed provider catalogue omits any existing episode row; Deluno does not silently merge shows or mix episode catalogues
- an accepted, reviewed remap refreshes provider-owned identity/catalogue fields while keeping files, monitoring, history, tags, numbering overrides, and plan assignments

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
- raw totals in the explicit release-explain diagnostic response are labelled
  legacy scoring provenance; they are not the typed release-preference decision
  value and are not used by normal candidate lists or upgrade decisions
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
- `GET /api/libraries/{id}/import-existing/preview?cursor=...&take=50` — reads a bounded page of existing files/folders without writing
- `POST /api/libraries/{id}/import-existing/selected` — imports only the explicitly selected preview paths (maximum 100 per request)
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
- each library download-client link may carry an optional `category`; when present, Deluno sends that label to the client for automatic grabs. When empty, the client’s configured Movies or TV category is used. This supports separate routes such as `family-movies`, `movies`, and `kids` on one SABnzbd or qBittorrent instance without duplicating global completed-download settings

`PUT /api/libraries/{id}/workflow` also accepts `cleanupMode` (`keep-source`
or `remove-source-after-import`) and `removeEmptySourceFolders`. Cleanup is
performed only after a verified import and is scoped below the library's
configured completed-download path. The default keeps the source. Download
client queue retention, repair, and seeding remain the external client's
responsibility unless a future client integration explicitly supports a safe
Deluno-owned action.

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

The normal user workflow is review-first: the Library & Storage drawer opens the
read-only preview, keeps only one page of candidates in the browser, and lets the
user import selected files or folders. The selected endpoint checks each path is
still inside the library root before writing. The older background endpoint remains
for resumable maintenance and compatibility, but the UI never starts it blindly.

Current gaps:

- there is no dedicated update-library endpoint yet beyond the specific sub-settings routes
- routing preview/explanation payloads can still get richer

## Filesystem Paths

Implemented endpoints:

- `GET /api/filesystem/directories?path=...` — server-visible drives and folders for advanced browsing
- `POST /api/filesystem/native-folder-picker` — opens the native Windows folder picker when Deluno is running in an interactive desktop session
- `POST /api/filesystem/path-diagnostics` — checks whether the Deluno server can read and write the selected path
- `POST /api/filesystem/import/preview` — returns the authoritative destination and media checks; a multi-file TV directory also returns `pack` with every source, canonical episode key, destination, and whole-pack block reason
- `POST /api/filesystem/import/jobs` — queues only a currently executable preview; an ambiguous or colliding TV pack is rejected before a job exists
- `POST /api/filesystem/import/execute` — stages every file in an executable TV pack, finalizes every unique destination, and records the episode manifest in one catalogue transaction

Multi-file TV import is deliberately all-or-nothing. A directory must identify one
season, every video must map to one or more episodes in the selected show's persisted
catalogue, and no episode or destination may be claimed by two files. Deluno places no
file when that review fails. Once approved, filesystem placement is compensated if the
single catalogue transaction fails: copied or linked destinations are removed and moved
sources are restored. A retry is reported as already committed only when every episode
still owns its reviewed path and the source and destination SHA-256 content match; file
size alone is not accepted as proof.

The native picker is available in the installed interactive Windows tray app and in an interactive Windows development host. It can select local folders, mapped drives, and UNC locations visible to the same Windows user session. When Deluno runs as a Windows service, in Docker, or on a non-Windows host, the endpoint reports that the picker is unavailable; the web UI then opens the advanced server browser and manual path entry instead. If the browser is remote, the native dialog belongs to the backend desktop session, so advanced browse or manual entry is the appropriate path. Server-side path visibility and permissions remain authoritative, especially for services and network shares.

The metadata refresh endpoints select candidates in SQL and return **honest counts**:
`enqueuedCount` is the batch primed now, `remainingCount` is what is still to go, and
`message` phrases both for display. `forceAll` marks the whole library as wanting a
refresh in one statement rather than queueing a page of jobs — the backfill then works
through it continuously. This is a breaking change: the response no longer carries a
`jobs` array, which on a large library was up to 1,000 job objects and reported a few
percent of the work as if it were all of it.

### The paged catalogue list

`GET /api/{movies,series}/page` takes `search`, `status`, `libraryId`, `sort`, `direction`,
`pageSize` and `pageToken`, and returns
`{ items, nextPageToken, hasMore, totalCount, facets }`.

- **Search, filter, sort and the counts all happen in SQL.** The list surface must
  not fetch the catalogue and work it out in the browser; that is what stops
  working as a library grows.
- **`pageToken` is keyset, not offset.** Page 400 costs what page 1 costs, and a row
  inserted while somebody scrolls cannot shift the window. An unreadable token means
  "start again", never an error — tokens travel in URLs and outlive deploys.
- **`totalCount` and `facets` are present on the first page only** (`null` on a
  continuation). Counting is the one part of the request that scans, so it happens
  once per filter rather than once per page.
- `libraryId` is optional. When present, the page, total count, status facets, and
  file facts are scoped to that library's wanted-state rows. Omitting it means all
  libraries, so the default view remains the complete Movies or TV catalogue.
- `sort` is one of `added` (default), `title`, `year`, `rating`; each has an index
  behind it. `status` is one of `all`, `monitored`, `unmonitored`, `downloaded`,
  `missing`, or `upgrades`. `pageSize` is capped at the shared maximum of 500;
  `hasMore` makes that bounded result explicit.
- Rows carry `fileSizeBytes` and `currentQuality`, plus file path, codecs, audio
  details, release group, runtime, popularity, votes, and derived bitrate. The legacy
  unbounded catalogue routes were removed; consumers must use this paged contract.

Saved library views also persist the optional `libraryId` alongside their search,
status, order, and display choices, so a view such as "Family movies" returns to the
same library without downloading or filtering the full catalogue in the UI.

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

Indexer request payloads accept `requestIntervalSeconds` as either `null` (the
safe two-second default) or an integer from 2 through 60. `PUT /api/indexers/{id}`
also accepts `clearRequestInterval: true` to return an existing indexer to that
default without changing its other settings. Deluno applies the selected interval
before every request to that indexer host.

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
- `POST /api/download-clients/{clientId}/categories/check` — verifies a library-specific category against SABnzbd or qBittorrent and reports `ready`, `missing`, `unreachable`, `configuration`, or `unsupported` without claiming the route is ready when it cannot be verified

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

- `GET /api/jobs?pageSize=n&pageToken=…`
- `GET /api/activity?pageSize=n&pageToken=…`
- `GET /api/decisions?pageSize=n&pageToken=…`
- `GET /api/library-automation?pageSize=n&pageToken=…`
- `GET /api/search-cycles?pageSize=n&pageToken=…`
- `GET /api/search-retry-windows?pageSize=n&pageToken=…`
- `GET /api/download-dispatches?pageSize=n&pageToken=…`
- `GET /api/v1/download-dispatches?pageSize=n&pageToken=…`
- `GET /api/v1/import-resolutions?pageSize=n&pageToken=…`
- `GET /api/download-health?pageSize=n&pageToken=…`
- `GET /api/integrations/external/manifest`
- `GET /api/integrations/external/health`
- `GET /api/integrations/external/queue`
- `GET /api/integrations/external/activity`
- `POST /api/integrations/external/trigger-refresh`
- `POST /api/integrations/processors/events`

Current UI contract expectations:

- activity and queue are already fed by durable job/activity stores, not just transient UI state
- every operational list returns `{ items, nextPageToken, hasMore }`; page tokens are opaque seek cursors and `pageSize` is capped at 500
- intake diagnostics return `{ source, diagnostics: { items, nextPageToken, hasMore } }`; catalogue pages add `totalCount` and `facets` on their first page
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
