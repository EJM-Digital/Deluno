# Automation integrations

Deluno exposes a versioned, authenticated API for personal automation. Use
`/api/v1` in integrations and send a least-privilege API key in `X-Api-Key`.
The same routes are described by the shipped document at
`/api/openapi/v1.json`; `/api/docs` is the interactive view.

The shipped-host contract test exercises this surface without writing test
titles. From the repository root, run it against the lab (or another
disposable shipped host):

```powershell
.\scripts\test-automation-contract.ps1 `
  -BaseUrl http://your-deluno-host:5099 `
  -Username admin `
  -Password '…'
```

It checks the OpenAPI route inventory, scope templates, readiness and summary,
then runs a mixed movie/TV dry-run containing one invalid item and replays its
idempotency key. A 503 readiness response is accepted because a newly
installed host may be deliberately paused at setup; all other assertions must
be successful.

## API-key scopes

Create a key from System → API access, or inspect the server-owned templates:

```text
GET /api/v1/api-keys/scope-templates
```

Recommended scopes are `read` for observation, `read,write,queue` for
automation and Home Assistant, and `read,write,queue,imports` for a native
mobile client that starts existing-library imports. Do not use `all` for an
external integration unless it is a trusted local operator.

## Mixed movie and TV catalogue add

The endpoint accepts at most 250 items, evaluates each item independently, and
routes created titles through the same library wanted-state and refresh logic as
the web app. Use an idempotency key for retries. A dry run never writes a title
or queues a refresh.

### curl

```bash
BASE_URL="http://deluno:5099"
DELUNO_API_KEY="replace-with-a-key"
IDEMPOTENCY_KEY="catalogue-import-2026-08-31"

curl --fail-with-body --silent --show-error \
  -X POST "$BASE_URL/api/v1/automation/catalogue/bulk" \
  -H "X-Api-Key: $DELUNO_API_KEY" \
  -H "Idempotency-Key: $IDEMPOTENCY_KEY" \
  -H "Content-Type: application/json" \
  --data @catalogue.json
```

`catalogue.json`:

```json
{
  "dryRun": true,
  "items": [
    { "clientItemId": "movie-1", "mediaType": "movie", "title": "The Matrix", "year": 1999, "imdbId": "tt0133093" },
    { "clientItemId": "series-1", "mediaType": "tv", "title": "The Expanse", "year": 2015, "imdbId": "tt3230854", "seriesType": "standard", "numberingScheme": "standard", "episodes": [{ "seasonNumber": 1, "episodeNumber": 1 }] }
  ]
}
```

The response contains one `items[]` result per input item. Statuses are
`would-create`, `created`, `already-exists`, `invalid`, or `failed`; retrying
the same key and body returns the stored response rather than adding another
title.

TV catalogue items may also provide `seriesType` (`standard`, `daily`, or
`anime`), `numberingScheme` (`standard`, `airdate`, `absolute`, or `scene`),
and `numberingSource`. Episode rows can carry the corresponding absolute or
scene keys. When a scheme is omitted, Deluno derives the safe default from the
series type (standard → standard, daily → airdate, anime → absolute).

### PowerShell

```powershell
$baseUrl = "http://deluno:5099"
$headers = @{ "X-Api-Key" = $env:DELUNO_API_KEY; "Idempotency-Key" = "catalogue-import-2026-08-31" }
$body = @{
  dryRun = $true
  items = @(
    @{ clientItemId = "movie-1"; mediaType = "movie"; title = "The Matrix"; year = 1999; imdbId = "tt0133093" }
    @{ clientItemId = "series-1"; mediaType = "tv"; title = "The Expanse"; year = 2015; imdbId = "tt3230854"; episodes = @(@{ seasonNumber = 1; episodeNumber = 1 }) }
  )
} | ConvertTo-Json -Depth 8

Invoke-RestMethod -Method Post -Uri "$baseUrl/api/v1/automation/catalogue/bulk" `
  -Headers $headers -ContentType "application/json" -Body $body
```

For a known series, sync explicit season/episode metadata with
`POST /api/v1/automation/series/{seriesId}/episodes/bulk`. It has the same
dry-run, per-item and idempotency behavior and is capped at 1,000 episodes.
Each episode may also include `absoluteNumber`, `sceneSeasonNumber`,
`sceneEpisodeNumber`, and `numberingSource` so an owner-supplied Anime,
Scene, or AirDate catalogue cannot lose its alternate identity during sync.

## Existing-library import

Existing files remain a reviewable import flow. Start the tracked operation,
poll it, and inspect issues; Deluno never guesses a destination from an
unreviewed path.

```bash
curl --fail-with-body -H "X-Api-Key: $DELUNO_API_KEY" \
  -X POST "$BASE_URL/api/v1/libraries/$LIBRARY_ID/import-existing"
curl --fail-with-body -H "X-Api-Key: $DELUNO_API_KEY" \
  "$BASE_URL/api/v1/libraries/$LIBRARY_ID/import-existing"
curl --fail-with-body -H "X-Api-Key: $DELUNO_API_KEY" \
  "$BASE_URL/api/v1/libraries/$LIBRARY_ID/import-existing/issues?take=100"
```

Pause/resume use `POST .../pause` and `POST .../resume`. The automation key
needs `read,write,queue`; a key that only reads progress cannot mutate the run.

Home Assistant can also pause or resume Deluno's global background automation
through `PUT /api/v1/settings/automation` with `{"isEnabled":false}` or
`{"isEnabled":true}`. This only holds or releases background work; it does not
delete media or cancel an already dispatched external-client job.

Webhook delivery failures are durable. Inspect pending, retrying, delivered or
dead-letter records without reading the database, then replay one saved payload
after correcting the destination:

```bash
curl --fail-with-body -H "X-Api-Key: $DELUNO_API_KEY" \
  "$BASE_URL/api/v1/notification-webhooks/deliveries?status=dead-letter&take=100"
curl --fail-with-body -H "X-Api-Key: $DELUNO_API_KEY" \
  -X POST "$BASE_URL/api/v1/notification-webhooks/deliveries/$DELIVERY_ID/replay"
```

## Search and queue inspection

```powershell
$headers = @{ "X-Api-Key" = $env:DELUNO_API_KEY }
Invoke-RestMethod -Method Post -Uri "$baseUrl/api/v1/libraries/$libraryId/search-now" -Headers $headers
Invoke-RestMethod -Uri "$baseUrl/api/v1/jobs?pageSize=50" -Headers $headers
Invoke-RestMethod -Uri "$baseUrl/api/v1/activity?pageSize=50" -Headers $headers
Invoke-RestMethod -Uri "$baseUrl/api/v1/decisions?pageSize=50" -Headers $headers
Invoke-RestMethod -Uri "$baseUrl/api/v1/download-dispatches?mediaType=movie&pageSize=50" -Headers $headers
Invoke-RestMethod -Uri "$baseUrl/api/v1/import-resolutions?status=failed&mediaType=movie&pageSize=50" -Headers $headers
```

Download-dispatch and import-resolution responses include the same typed
`failure` object used by health and notification APIs. That preserves the
external service, operation, stable kind, retry state, and external client
identifier through the queue and import views instead of reducing the cause
to an unscoped string.

The movie and TV title endpoints also expose their normal per-title and bulk
search actions; callers should use the OpenAPI document for their exact request
shape rather than scraping the web UI.

## Backup

Backups are system-scoped because they contain installation configuration. Use
a separate trusted key with `system` when a script needs to create or inspect a
backup:

```bash
curl --fail-with-body -H "X-Api-Key: $DELUNO_SYSTEM_API_KEY" \
  -H "Content-Type: application/json" \
  -X POST "$BASE_URL/api/v1/backups" \
  --data '{"reason":"before-automation-change"}'
curl --fail-with-body -H "X-Api-Key: $DELUNO_SYSTEM_API_KEY" \
  "$BASE_URL/api/v1/backups"
```

## Supported SDK

The dependency-free TypeScript client is in `sdk/typescript` and is
typechecked in CI. It exposes the bulk catalogue/episode operations, readiness,
 library import, search, queue, dispatch/import history, backup and
 scope-template methods:

```ts
import { DelunoClient } from "@deluno/api-client";

const deluno = new DelunoClient({
  baseUrl: process.env.DELUNO_URL!,
  apiKey: process.env.DELUNO_API_KEY!
});

const preview = await deluno.bulkAddCatalogue({
  dryRun: true,
  idempotencyKey: "catalogue-import-2026-08-31",
  items: [{ mediaType: "movie", title: "The Matrix", year: 1999 }]
});

const releasePreview = await deluno.previewReleasePreference({
  planId: "movies-default",
  releaseName: "The.Matrix.1999.2160p.WEB-DL.DDP5.1.Atmos.H.265",
  currentReleaseName: "The.Matrix.1999.1080p.WEB-DL.DDP5.1.H.264"
});

const failedWebhooks = await deluno.listNotificationWebhookDeliveries({
  status: "dead-letter",
  take: 100,
});
if (failedWebhooks[0]) {
  await deluno.replayNotificationWebhookDelivery(failedWebhooks[0].id);
}

const failedImports = await deluno.listImportResolutions({
  status: "failed",
  mediaType: "movie",
  pageSize: 50,
});
const dispatch = failedImports.items[0]
  ? await deluno.getDownloadDispatch(failedImports.items[0].dispatchId)
  : undefined;
```

`previewReleasePreference` returns typed facts, hard-gate status, per-family
explanations, and (when a current release is supplied) a typed comparison. It
does not expose the old aggregate score as a decision value.

Do not put API keys in source control, URLs, or request bodies.
