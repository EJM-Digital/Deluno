# External Integration API

Deluno exposes an integration API so trusted local tools can understand and coordinate with a Deluno install without reading Deluno databases or scraping UI state.

## Authentication

The integration API uses Deluno's authenticated API boundary.

For external server-side tools, generate a key in Deluno under **System -> API** and send it with every request:

```http
X-Api-Key: deluno_generated_key_here
```

Clients that only support bearer auth can also send:

```http
Authorization: Bearer deluno_generated_key_here
```

Deluno stores only a hash of generated API keys. The raw key is shown once at creation time. Revoking a key takes effect immediately.

## Rate Limits

A request presenting a generated API key (`X-Api-Key`, or `Authorization: Bearer deluno_...`) is subject to a global rate limit, in addition to the stricter limit on `/api/auth/login`. The default budget is **3000 requests per 60-second window**, partitioned per API key — not shared across different callers.

This limit exists to protect Deluno from a misbehaving *external* script or integration. It does **not** apply to Deluno's own web UI: a request presenting the browser's own login session token (not a `deluno_`-prefixed API key) is exempt entirely, so it is never affected by how many tabs of the same login are open or how often the dashboard polls. Static assets, `/hubs`, and `/api/metadata/artwork` are exempt for everyone. A fully unauthenticated request (no credential presented at all — most `/api` routes require one) falls back to a per-remote-address limit as a defensive floor.

Configure it with:

```json
{
  "Security": {
    "Api": {
      "PermitLimit": 3000,
      "WindowSeconds": 60
    }
  }
}
```

A request over the limit gets `429 Too Many Requests`, a `Retry-After` header naming the window in seconds, and a JSON body:

```json
{ "error": "Rate limit exceeded.", "retryAfterSeconds": 60 }
```

There is no back-pressure signal beyond this — back off and retry after the window rather than retrying immediately.

## OpenAPI And Interactive Docs

Deluno publishes machine-readable API docs and an interactive Swagger UI:

- `GET /api/openapi/v1.json`
- `GET /api/docs`

Use `/api/openapi/v1.json` as the contract source for generated clients and integration validation.

## Versioning

`/api/v1/...` is the stable form to code against. The bare `/api/...` form is an unversioned alias that always tracks the newest version Deluno currently serves — convenient for local scripts, but it can change shape across a Deluno update without notice.

Every response — success or error — carries an `X-Deluno-Api-Version` header naming the version that served it (currently `v1`). Requesting an unsupported version (`/api/v2/...` today) returns `400 Bad Request` with an explicit message, not a `404`, so a client can tell "this version doesn't exist yet" apart from "this route doesn't exist".

## Webhooks

### Download-client inbound webhook

Endpoint:

- `POST /api/download-clients/{clientId}/webhook`

Payload:

```json
{
  "event": "completed",
  "dispatchId": "optional-dispatch-id",
  "hash": "optional-client-hash",
  "name": "optional-release-name",
  "savePath": "optional-final-path",
  "sizeBytes": 1234567890,
  "failureReason": "optional-failure-text"
}
```

Resolution order for dispatch matching:

1. `dispatchId`
2. `hash`
3. `name`

Event normalization:

- completion aliases map to `completed` (`download.completed`, `torrent_completed`, `finished`, etc.)
- failure aliases map to `failed` (`download.failed`, `torrent_failed`, `error`, etc.)

Idempotency and duplicate handling:

- duplicate completion webhook for an already-detected dispatch is accepted but ignored
- duplicate failure webhook for a dispatch with final import outcome is accepted but ignored
- unmatched webhook payloads return a not-found result with a safe message

### Notification outbound webhook

Configured via:

- `GET|POST|PUT|DELETE /api/notification-webhooks`
- `POST /api/notification-webhooks/{id}/test`

Delivery behavior:

- event filters are prefix-based (`movies`, `series`, `health`) and support `*` for all
- Discord webhook URLs receive Discord embed payloads
- other URLs receive a generic JSON payload with event metadata
- delivery retries are attempted up to three times for transient failures:
  - attempt 1: immediate
  - attempt 2: after 2 seconds
  - attempt 3: after 5 seconds
- final outcome is recorded on the webhook row:
  - success updates `last_fired_utc`
  - failure stores `last_error`

## Manifest

`GET /api/integrations/external/manifest`

Returns Deluno's media operations manifest.

Use this first. It tells the calling tool what Deluno is configured to manage.

Response shape:

```json
{
  "product": "Deluno",
  "version": "v1",
  "instanceName": "Deluno",
  "capabilities": [
    "movies",
    "tv",
    "indexers",
    "download-clients",
    "library-routing",
    "destination-rules",
    "metadata",
    "media-probing",
    "pre-import-processing",
    "activity-feed",
    "signalr"
  ],
  "recommendedCategories": {
    "movies": "deluno-movies",
    "tv": "deluno-tv",
    "anime": "deluno-anime",
    "movies4k": "deluno-movies-4k",
    "tv4k": "deluno-tv-4k"
  },
  "libraries": [],
  "indexers": [],
  "downloadClients": [],
  "connections": []
}
```

## Operational Endpoints

These endpoints are intentionally generic so processors, automation scripts, dashboards, and future tools can all use the same contract.

- `GET /api/integrations/external/health` returns instance health, configured library counts, enabled provider counts, active jobs, and problem count.
- `GET /api/integrations/external/queue` returns current jobs plus recent download dispatches.
- `GET /api/integrations/external/activity` returns recent Deluno activity events.
- `POST /api/integrations/external/import-preview` runs the same destination-rule/import preview engine used by Deluno's queue UI.
- `POST /api/integrations/external/trigger-refresh` requests library search refreshes by media type.
- `POST /api/integrations/processors/events` reports generic processor status for refine-before-import workflows.
- `POST /api/intake-sources/{id}/sync` queues an immediate watchlist/intake sync for a configured source.
- `GET /api/intake-sources/{id}/diagnostics?pageSize=50&pageToken=…` returns one explicit page of recent sync diagnostics and skip reasons for that source.
- `GET /api/intake-title-origins?mediaType=movies|tv&entityId={id}` returns durable, non-secret import-list provenance for one title.
- `GET /api/monitoring/dashboard` returns current readiness, storage, provider health, performance, and active alert state.
- `GET /api/monitoring/alerts` returns active monitoring rule violations (services down, low storage, elevated failure rate).
- `GET /api/monitoring/diagnostics?query=failed&pageSize=100&pageToken=…` searches one explicit page of operational activity diagnostics.
- `GET /api/monitoring/export/prometheus` returns Prometheus text exposition for Deluno monitoring gauges.
- `GET /api/monitoring/export/influx` returns Influx line protocol for Deluno monitoring gauges.
- `GET /api/ranking-model/status` returns ML ranking runtime status (enabled state, active version, evaluation metrics).
- `POST /api/ranking-model/train` triggers an immediate retraining pass on labeled dispatch telemetry.
- `POST /api/ranking-model/rollback` rolls back the active model version.
- `GET /api/intelligent-routing/snapshot` returns learned preference profile plus indexer/client success-rate maps.
- `GET /api/intelligent-routing/anomalies` returns detected unusual grab/failure/downgrade patterns.
- `POST /api/intelligent-routing/recommend-release` returns a recommendation score for a proposed release using rules + ML + historical routing success.

API key scopes:

- `read` can call health, manifest, queue, and activity endpoints.
- `imports` can call import preview.
- `imports` can list processor hand-offs and report processor events.
- `queue` can trigger refreshes and queue-related actions.
- `all` can call everything.

## Refine Before Import

When a Deluno library is configured for **Refine before import**, an external processor can clean the completed download without bypassing Deluno's destination resolver, import mover, rename rules, or metadata refresh. The normal path is processor-agnostic: the processor writes below the library's configured processed-output root using the completed download's final source-folder name, and Deluno matches that stable path component to its durable hand-off.

### Optional notification callback

In **Library setup → Media management → Optional completion callbacks**, save a compatible generic webhook only if existing automation should be notified when a completed download reaches the hand-off stage. Deluno posts this minimal payload; it does not configure, start, or otherwise integrate with FileFlows, Weir, or another processor:

```json
{
  "eventType": "deluno.processor-handoff",
  "handoffId": "handoff-id",
  "libraryId": "library-id",
  "mediaType": "movies",
  "sourcePath": "D:\\Downloads\\Blade.Runner.2049",
  "releaseName": "Blade.Runner.2049",
  "queueItemId": "download-client-queue-id",
  "callbackPath": "/api/integrations/processors/events"
}
```

The selected connection may use an `Authorization: Bearer …` token or a custom header. Deluno never includes that secret in activity, diagnostics, or API responses. A connection test uses a safe `HEAD` request; a processor that rejects `HEAD` is still shown as reachable, and its first actual hand-off validates the submission path.

Any existing automation can use `sourcePath` as its input, preserve `handoffId`, and optionally call Deluno back when it has produced its clean output. Treat `handoffId` as the idempotency key: if automation sees it again after a Deluno restart, it must not run the same file twice. Deluno will not import merely because the outbound POST succeeded.

Endpoint:

```http
POST /api/integrations/processors/events
Authorization: Bearer deluno_generated_key_here
Content-Type: application/json
```

Example:

```json
{
  "libraryId": "library-id",
  "mediaType": "movies",
  "entityType": "movie",
  "entityId": "Blade Runner 2049",
  "sourcePath": "D:\\Downloads\\Blade.Runner.2049",
  "outputPath": "D:\\Deluno\\Refined\\Blade.Runner.2049.clean.mkv",
  "status": "completed",
  "message": "Removed unwanted audio and subtitles.",
  "processorName": "External Refiner"
}
```

Before processing, read the durable hand-off that Deluno created for the completed download:

```http
GET /api/integrations/processors/handoffs?libraryId=library-id
Authorization: Bearer deluno_generated_key_here
```

Pass its `id` back as `handoffId` in every processor event. `sourcePath` remains useful as a second correlation check, but Deluno will not create a new hand-off from a callback.

Supported statuses are `accepted`, `started`, `completed`, and `failed`. Completed events must include `outputPath`; failed events should include a user-readable `message`.

### Completion safety boundary

Deluno accepts a completed event only when all of these are true:

1. `libraryId` names an existing library configured for **Refine before import**.
2. The event matches a durable waiting hand-off by `handoffId` or the exact recorded `sourcePath`.
3. `outputPath` is inside that library's configured **Clean output folder**.
4. The caller is signed in, or uses an API key with the `imports` scope.

Deluno will not infer a library from `mediaType`, accept a sibling/prefix path, create an unknown hand-off, or queue the same active output import twice. A repeated completed callback reuses the active import job. A callback is optional: the normal watched-output path imports exactly one video below `processed-output/<source-folder>/...` when that folder matches a waiting Deluno hand-off. Ambiguous, unmatched, or top-level files become recovery items for human review rather than being guessed at.

## Why This Exists

External tools should not need to know Deluno internals.

The manifest answers:

- Which libraries are movies vs TV?
- Which roots are configured?
- Which download clients exist?
- Which categories should be used for movies and TV?
- Which indexers are enabled?
- Which integration hooks exist?
- Which capabilities can external tools rely on?

## Intended Workflow

1. The external tool authenticates to Deluno.
2. The external tool reads the manifest.
3. The external tool stores Deluno library/client/source IDs only as integration references.
4. The external tool uses Deluno APIs for queue, activity, import preview, processor events, refresh, and orchestration instead of direct DB access.

## Implemented Import Coordination

Deluno now supports import coordination without requiring tools to read internal state:

- processor events can queue clean-output imports only within the library-owned output boundary
- processor output folder checks report uncorrelated finished files for recovery; they never auto-import a file that has no matching hand-off
- processor timeouts create recovery cases for Queue/Activity review
- import preview uses the same destination resolver as the app UI

## Design Rule

Deluno remains the source of truth for media management decisions.

External tools can observe, summarize, coordinate, and request actions, but they should not duplicate Deluno's routing, import, quality, or metadata decision engines.
