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
