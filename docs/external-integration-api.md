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

## OpenAPI And Interactive Docs

Deluno publishes machine-readable API docs and an interactive Swagger UI:

- `GET /api/openapi/v1.json`
- `GET /api/docs`

Use `/api/openapi/v1.json` as the contract source for generated clients and integration validation.

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
  "version": "1",
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
- `GET /api/intake-sources/{id}/diagnostics?take=50` returns recent sync diagnostics and skip reasons for that source.
- `GET /api/monitoring/dashboard` returns current readiness, storage, provider health, performance, and active alert state.
- `GET /api/monitoring/alerts` returns active monitoring rule violations (services down, low storage, elevated failure rate).
- `GET /api/monitoring/diagnostics?query=failed&take=100` searches operational activity diagnostics.
- `GET /api/monitoring/export/prometheus` returns Prometheus text exposition for Deluno monitoring gauges.
- `GET /api/monitoring/export/influx` returns Influx line protocol for Deluno monitoring gauges.
- `GET /api/ranking-model/status` returns ML ranking runtime status (enabled state, active version, evaluation metrics).
- `POST /api/ranking-model/train` triggers an immediate retraining pass on labeled dispatch telemetry.
- `POST /api/ranking-model/rollback` rolls back the active model version.

API key scopes:

- `read` can call health, manifest, queue, and activity endpoints.
- `imports` can call import preview.
- `queue` can trigger refreshes and queue-related actions.
- `imports` or `queue` can report processor events.
- `all` can call everything.

## Refine Before Import

When a Deluno library is configured for **Refine before import**, an external processor can clean the completed download and report progress back to Deluno without bypassing Deluno's destination resolver, import mover, rename rules, or metadata refresh.

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

Supported statuses are `accepted`, `started`, `completed`, and `failed`. Completed events must include `outputPath`; failed events should include a user-readable `message`.

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

- processor events can queue clean-output imports
- the processor output folder watcher can detect finished files when an event was not sent
- processor timeouts create recovery cases for Queue/Activity review
- import preview uses the same destination resolver as the app UI

## Design Rule

Deluno remains the source of truth for media management decisions.

External tools can observe, summarize, coordinate, and request actions, but they should not duplicate Deluno's routing, import, quality, or metadata decision engines.
