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
- `GET /api/notification-webhooks/deliveries?status=dead-letter&take=100`
- `POST /api/notification-webhooks/deliveries/{deliveryId}/replay`

Delivery behavior:

- event filters are prefix-based (`movies`, `series`, `health`) and support `*` for all
- Discord webhook URLs receive Discord embed payloads
- other URLs receive a generic JSON payload with event metadata
- an event about one title (movie, series or episode) carries that title's tag
  names: the generic payload has a top-level `tags` array of names (empty when
  the title has no tags, and also copied into `details.tags`); chat providers
  (Discord, Slack, Telegram, Pushover, ntfy, Gotify, Apprise) append a
  `Tags: a, b` line to the message when the title has tags. Events that name
  no title (health, library paused, app update) have no `tags` key at all.
  Deluno writes no `.nfo` files, so there is nothing to tag there.
- delivery retries are attempted up to three times for transient failures:
  - attempt 1: immediate
  - attempt 2: after 2 seconds
  - attempt 3: after 5 seconds
- final outcome is recorded on the webhook row:
  - success updates `last_fired_utc`
  - failure stores `last_error`
- every attempted delivery is also stored before the outbound request, with its
  event payload, status (`pending`, `retrying`, `delivered`, or `dead-letter`),
  attempt count, next retry time, HTTP status and last error. A dead-letter
  response can be replayed explicitly by an authenticated caller; replay uses
  the original payload and a fresh bounded retry budget.

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
    "family-movies": "deluno-family-movies",
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

### Guide package

`GET /api/v1/guides/trash/package` returns the backend-owned, versioned
TRaSH-derived quality package used by Deluno's setup and acquisition surfaces.
Callers should retain the returned `version`, `source.upstreamRevision`, and
`integritySha256` with any generated configuration or preview. Items marked
`advanced` are intentionally not safe to reinterpret as typed preference
traits.

### Typed integration failures

Integration responses and operational history carry a typed `failure` (or
`lastFailure`) when a provider or client boundary did not complete. The object
identifies the stable `kind` (`authentication`, `rate-limit`, `timeout`,
`unavailable`, `malformed-response`, `rejected-action`, or `unknown`), the
`state` (`active`, `retry-scheduled`, `circuit-open`, `resolved`, or
`permanent`), `service`, `operation`, provider/client detail, an optional HTTP
status, `retryAfterUtc`, and a plain-English `nextAction`. Secrets and request
credentials are never included.

A failure is not the same thing as an empty result. Callers should retain the
typed failure alongside the final outcome and use `retryAfterUtc` or the
service health endpoint before retrying. Successful fallback results may still
include earlier provider failures for diagnosis, but their top-level `failure`
is null when the requested operation completed successfully. Subtitle and
metadata status/test endpoints expose the most recent typed provider failure;
queue, activity, and history records preserve the failure through restart.

- `GET /api/integrations/external/health` returns instance health, configured library counts, enabled provider counts, active jobs, and problem count.
- `GET /api/integrations/external/queue` returns current jobs plus recent download dispatches.
- `GET /api/integrations/external/activity` returns recent Deluno activity events.
- `POST /api/integrations/external/import-preview` runs the same destination-rule/import preview engine used by Deluno's queue UI.
- `POST /api/integrations/external/trigger-refresh` requests library search refreshes by media type.
- `POST /api/integrations/external/file-changed` tells Deluno a tool changed one library file in place, so Deluno re-reads it. See [A library file changed](#a-library-file-changed).
- `POST /api/integrations/external/redownload` asks Deluno to download a title again: the file goes to the recycle bin and the title is searched for. See [Download a title again](#download-a-title-again).
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
- `imports` can call import preview, report a changed library file, and ask for a title to be downloaded again.
- `imports` can list processor hand-offs and report processor events.
- `queue` can trigger refreshes and queue-related actions.
- `all` can call everything.

### A library file changed

A tool that edits files already in the library, such as Weir removing unwanted
audio tracks, calls this after it swaps the file in place. Deluno's record of
the file (size, codecs, audio and subtitle languages) is otherwise stale.

```http
POST /api/integrations/external/file-changed
X-Api-Key: <key with the imports scope>
Content-Type: application/json

{ "path": "D:\Media\Movies\Blade Runner 2049 (2017)\Blade Runner 2049.mkv",
  "reason": "removed 2 audio tracks",
  "tool": "Weir" }
```

- `path` must be a file Deluno tracks, matched exactly but ignoring case.
- Deluno takes the file's current size and marks its streams as owed a fresh
  read. The media probe and the subtitle scan run for it on their own lanes.
- Nothing is searched, upgraded, renamed or imported because of this call. If
  the fresh read changes whether a title meets its cutoff, that follows the
  library's normal schedule.
- Activity records "Weir changed Blade Runner 2049.mkv: removed 2 audio tracks.
  Deluno is re-reading it."

Responses:

| Status | Meaning |
|---|---|
| `202` | Accepted. `titles` lists the movie or series rows holding the file. `coalesced: true` means the same path was reported less than a minute ago and is already being re-read. |
| `400` | `path` is empty. |
| `404` | No file exists at `path`, or Deluno does not track it. |

The manifest advertises this as the `external-file-changed` capability.

Not `trigger-refresh`: that asks for library searches, which can download
things.

### Download a title again

A tool that removes things from library files, such as Weir removing audio
tracks, cannot put them back: a removal is final. When someone later wants a
removed track back ("start keeping Japanese audio"), only a new download brings
it, and a search alone finds nothing because the library already holds the
title. So the file has to go first.

```http
POST /api/integrations/external/redownload
X-Api-Key: <key with the imports scope>
Content-Type: application/json

{ "path": "D:\Media\Movies\Blade Runner 2049 (2017)\Blade Runner 2049.mkv",
  "reason": "Keep Japanese audio (removed earlier by Weir)",
  "tool": "Weir" }
```

In order, and nothing further once a step fails:

1. `path` must be a library file Deluno tracks, matched exactly (ignoring case
   on Windows).
2. The file goes to Deluno's recycle bin, its normal delete, and can be put
   back while the bin keeps it. If it cannot be moved, nothing else happens.
3. The title (or episode) is marked missing.
4. Whatever would stop the new download is cleared, as a person's
   **Force a re-download** clears it. Above all, the download client is told to
   forget the finished download it still remembers and would otherwise quietly
   refuse.
5. One search for that title is queued. The new copy comes in through the
   library's usual workflow, including processing (for example by Weir with its
   current rules) when the library waits for processing. A copy below the
   profile's cutoff is held for a person, as it is for every search.

Activity records "Weir asked Deluno to download Blade Runner 2049 again: Keep
Japanese audio (removed earlier by Weir). The file is in the recycle bin and a
search has started."

Responses:

| Status | Meaning |
|---|---|
| `202` | Done. `{ "titleId", "mediaKind" ("movie" or "series"), "searchJobId", "fileRemoved": true, "message" }`. `searchJobId` is `null` if the file was removed but the search could not be queued; `message` says so, and the title can be searched from its page. |
| `400` | `path` is empty. |
| `404` | Deluno does not track a library file at `path`. Nothing changed. |
| `409` | The file could not be moved to the recycle bin (in use, permissions). Nothing changed and nothing was searched. |

The manifest advertises this as the `external-redownload` capability. A tool
should offer the action only when it is present.

## Refine Before Import

When a Deluno library is configured for **Refine before import**, an external processor can clean the completed download without bypassing Deluno's destination resolver, import mover, rename rules, or metadata refresh. The normal path is processor-agnostic: the processor writes below the library's configured processed-output root using the completed download's final source-folder name, and Deluno matches that stable path component to its durable hand-off.

### Optional processor connection

In **Media Management → Processing Workflow → Processor connections**, connect a compatible processor endpoint only when an external tool should receive processing jobs from Deluno. Deluno posts this minimal hand-off payload; the processor then reports completion to Deluno through the callback path in the payload. Deluno does not configure, start, or otherwise take ownership of FileFlows, Weir, or another processor:

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

Any existing automation can use `sourcePath` as its input, preserve `handoffId`, and call Deluno back when it has produced its clean output. Treat `handoffId` as the idempotency key: if automation sees it again after a Deluno restart, it must not run the same file twice. Deluno will not import merely because the outbound POST succeeded. If no processor connection is configured, Deluno watches the processed-output folder itself instead.

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

#### Reporting every output file

A hand-off gets one completion report, sent once every file in it is finished — not one report per file. `outputFiles` lists every file the processor produced for the hand-off, alongside `outputPath`:

- For a **folder hand-off**, `outputPath` names the hand-back folder and `outputFiles` lists every file inside it that belongs in the library (samples are never included). Deluno imports the folder as one operation — the same season-pack import path an already-placed download folder uses — instead of treating each file as a completion of its own.
- A **single-file hand-off** still carries `outputFiles`, with exactly one entry matching `outputPath`.
- `outputFiles` is optional, for a processor that predates it: Deluno imports whatever `outputPath` names and does not check it against a file list.

```json
{
  "libraryId": "library-id",
  "handoffId": "handoff-id",
  "status": "completed",
  "outputPath": "D:\\Deluno\\Refined\\Pioneer.One.Season.1",
  "outputFiles": [
    "D:\\Deluno\\Refined\\Pioneer.One.Season.1\\Pioneer.One.S01E01.mkv",
    "D:\\Deluno\\Refined\\Pioneer.One.Season.1\\Pioneer.One.S01E02.mkv"
  ],
  "processorName": "External Processor"
}
```

Deluno checks `outputFiles` against what is actually there. A named file it cannot find is not passed over quietly: the hand-off records the problem, and it surfaces on the download in Transfers instead of being reported as a plain success.

**Partial success.** When a processor only finishes some of a hand-off's files, it reports `status: "completed"` for the outputs it made when every file finished, or `status: "failed"` with a `message` giving the counts and `outputFiles` listing only the files that did succeed when some did not. Deluno still imports the files a failed report does name — a partial failure does not throw away the work the processor already did — and keeps the failure message so a person can see how many did not make it.

A second completion for a hand-off Deluno has already completed is refused, not silently accepted: Deluno expects one completion report per hand-off, naming every output up front, not a stream of one-off updates.

#### Failure dispositions

A `failed` event may say what the processor did with the file:

| Field | Values | Deluno's response |
|---|---|---|
| `disposition` | `held` | The processor kept the original. Deluno records the failure and raises a Queue recovery case. |
| | `rejected` | The processor deleted the download. Deluno refuses that release, tells the download client to forget it, searches for another copy, and answers `202`. If it cannot identify the release or record the refusal, it answers `409` and has done nothing, so the processor should keep the original. A repeat report for a hand-off already rejected answers `202` with `alreadyHandled: true` and does nothing new. |
| `sourceRemoved` | `true` / `false` | Whether the file at `sourcePath` is gone. Informational. |
| `failureClass` | e.g. `preflight`, `guardrail`, `execution` | Recorded with the refusal reason. |

A processor should send `rejected` only when the manifest's `capabilities` include `processor-reject-regrab`.

#### Asking the processor instead of timing it out

Deluno does not give a processor a fixed time to finish. When the connection's submission URL has the shape `…/intake/webhook/{source}`, Deluno reads `…/intake/capabilities` with the same authentication. If that lists `handoff-status`, Deluno asks `GET …/intake/handoffs/{source}/{handoffId}` on every planning pass and expects:

`…/intake/capabilities` may also answer with an optional, generic `watchedPath` field naming the folder the processor is actually watching for finished downloads:

```json
{
  "capabilities": ["handoff-status", "handoff-cancel", "handoff-outcome"],
  "watchedPath": "/data/processing/incoming"
}
```

This is not Weir-specific: any processor may report it, in whatever path shape makes sense on its own side of the connection. Deluno's health check (#909) compares it against the library's configured processed-output folder and raises a problem when they disagree — a processor that stopped watching the folder Deluno hands files to otherwise looks identical to one still working, right up until nothing gets imported. A processor that does not send this field is not treated as broken; the check shows "not reported by the processor" instead.

```json
{
  "handoffId": "…",
  "state": "queued | scheduled | working | completed | passed-through | failed | rejected | cancelled",
  "queuePosition": 3,
  "scheduledFor": "2026-09-17T01:00:00Z",
  "lastChangedUtc": "2026-09-16T10:00:00Z",
  "outputPath": null,
  "message": "Outside this library's processing hours."
}
```

- `queued`, `scheduled` and `working` are waited for, and Queue shows what the processor said.
- `404` means the processor never received the hand-off. Deluno stops waiting and offers **Import original**.
- `403` is shown as "set a webhook secret".
- A processor that cannot be reached is waited for. The time it is unreachable does not count.
- When nothing has changed for the library's **No progress for** setting (24 hours to 7 days, 48 hours by default) while the processor answers and has no scheduled run ahead, Deluno first calls `DELETE` on the same URL if `handoff-cancel` is listed. A `409` means it is working, so Deluno keeps waiting. Then the library's **If processing stalls or fails** rule applies: import the original, or leave it for review in Queue.
- A processor without these endpoints is judged the same way, from the hand-off's last recorded change.
- Whichever way the wait ends, a processed file that appears in the output folder afterwards is still imported.

`POST /api/integrations/processors/handoffs/{id}/import-original` imports the untouched download for a hand-off the processor did not finish.

#### Telling the processor what became of the file

If `…/intake/capabilities` lists `handoff-outcome`, Deluno tells the processor what became of each file it handed back, so the processor can let go of its copy:

```http
POST …/intake/handoffs/{source}/{handoffId}/outcome
{ "outcome": "imported | not-imported", "occurredUtc": "…", "importedPath": "…", "reason": "…" }
```

`not-imported` is final and is sent only when Deluno will not try the import again. `occurredUtc` is when the import finished, so every repeat of a report is the same report. `reason` is why, on `not-imported`; on `imported` it is set only for a season pack that left some of its files alone - an extra, featurette, sample or a special the catalogue does not list - and names them, in plain words, so the processor is told the whole story about what it gets to release.

Deluno keeps an outcome until the processor hears it:

- `2xx` is acknowledged. `released: true` in the body means the processor let go of its copy.
- `404`, and any other `4xx` except those below, are final. Repeating the same report would get the same answer.
- `409`, `401`, `403`, `408`, `429`, `5xx` and a processor that cannot be reached are all tried again. The first retry comes after a minute, and the wait doubles each time up to an hour.
- Deluno gives up after seven days without an acknowledgement, or when the processor connection is removed.
- A processor that answers but does not list `handoff-outcome` is not told, and keeps its copy until its own retention reclaims it.

Each hand-off in `GET /api/integrations/processors/handoffs` carries an `outcome` record: what was said, whether it was heard (`pending`, `delivered`, `refused` or `given-up`), how many attempts there have been, when the next is due, and the processor's last words. `POST /api/integrations/processors/handoffs/{id}/outcome/send-again` starts the report over, with a new count of attempts and a new week.

### Completion safety boundary

Deluno accepts a completed event only when all of these are true:

1. `libraryId` names an existing library configured for **Refine before import**.
2. The event matches a durable waiting hand-off by `handoffId` or the exact recorded `sourcePath`.
3. `outputPath` is inside that library's configured **Clean output folder**.
4. The caller is signed in, or uses an API key with the `imports` scope.

Deluno will not infer a library from `mediaType`, accept a sibling/prefix path, create an unknown hand-off, or queue the same active output import twice. A repeated completed callback reuses the active import job. A callback is optional: the normal watched-output path imports exactly one video below `processed-output/<source-folder>/...` when that folder matches a waiting Deluno hand-off. Ambiguous, unmatched, or top-level files become recovery items for human review rather than being guessed at.

A completed event refused for boundary 3 above, or because the named file demonstrably belongs to another hand-off, is told on Activity immediately (#891) — once per hand-off, not on every repeat of the same broken report — rather than only surfacing once the hand-off's ordinary stall timeout gives up on it, which can take up to a week. Nothing is blocklisted and no file is touched; this is only ever a notice that the processor's path mapping likely needs fixing.

### Temporary output names (#915)

A processor that reports completion through the callback is safe already: Deluno only ever reads `outputPath`/`outputFiles` once that report arrives. A drop-folder processor — one with no processor connection configured, whose output Deluno watches directly — has no callback to say "this file is finished," so Deluno instead waits for a file's size to stop changing for 20 seconds before treating it as importable.

A processor that writes a file in place can pause mid-write for longer than that without anything being wrong — a throttled disk, a paused job queue — and a 20-second gap alone cannot tell that apart from a finished file. **A drop-folder processor must therefore write its output under a temporary name and rename it to its final name only once the file is complete** — for example `Film.mkv.partial`, renamed to `Film.mkv` when done. Deluno's own import extension allow-list already ignores any file whose name does not end in a supported video extension, so `.partial`, `.tmp`, `.temp`, `.part` and `.crdownload` (or any other suffix that is not itself a supported extension) are never matched for import — the rename is what releases the file, not a size check. The 20-second stability check remains in place as a backup, for a processor that cannot follow this convention.

### Hand-off cleanup after import (#915, sheet row H4)

The processor's copy in the processed-output folder is only ever a working copy. After Deluno successfully imports from it — or a hand-off is abandoned (import-original, given up, or released) — Deluno removes everything that hand-off left in the processed-output folder: the output it imported, any temporary/partial file the processor never finished renaming, scrap files (`.nfo`, samples, subtitles it does not want), and the hand-off's own now-empty folder. This happens regardless of the library's Cleanup mode, which answers a different question — what happens to the *original* download, still governed by the sharing rule (#288). Only the files belonging to that one hand-off are ever touched: a hand-off in its own subfolder (the documented folder-hand-off shape) owns that subfolder outright; a loose file sharing the processed-output root with other hand-offs only ever loses files with its own name and a temporary suffix, never a sibling hand-off's files. Every cleanup is written to Activity.

## Cloudflare Helper (FlareSolverr / Byparr)

Deluno does not solve Cloudflare (or DDoS-Guard) challenges itself. When a tracker sits behind one, Deluno calls out to a helper the person runs — FlareSolverr, or a drop-in replacement such as Byparr that answers the same `/v1` API — whose address is set once under **Settings -> General -> Cloudflare helper**. This section documents exactly what Deluno sends and reads, checked against each helper's own source rather than its wiki, because #972 found the wiki out of date with what the code actually does.

**Checked against:**
- FlareSolverr [`v3.5.2`](https://github.com/FlareSolverr/FlareSolverr/tree/v3.5.2) — `src/flaresolverr_service.py`, `src/dtos.py`
- Byparr [`v3.0.4`](https://github.com/ThePhaseless/Byparr/tree/v3.0.4) — `src/endpoints.py`, `src/models.py`

Deluno's client code: `src/Deluno.Integrations/Search/Trackers/CloudflareHelper.cs` (`FlareSolverrCloudflareHelper`), and the reachability health check in `src/Deluno.Worker/HealthChecks/Checks/HttpProxyReachabilityTester.cs`.

### Endpoint and commands

Every command is a single `POST` to `<CloudflareHelperUrl>/v1` (the `/v1` suffix is appended if the configured address does not already end in it) with a JSON body carrying a `cmd` field. Deluno sends four:

| `cmd` | When Deluno sends it | Request fields it sends |
| --- | --- | --- |
| `sessions.create` | Before the first request to a host, so the helper's browser session (and any Cloudflare clearance it earns) is kept between requests instead of solved from scratch every time (49s on the lab rig, vs. seconds with a warm session) | `session` |
| `request.get` | Fetching a page (`ICloudflareHelper.FetchAsync`) | `cmd`, `url`, `session`, `maxTimeout`, `proxy` (only when the indexer has one assigned, #748/#749) |
| `request.post` | Fetching a page that only passes its Cloudflare challenge by posting form data (`ICloudflareHelper.PostAsync`) | same as `request.get`, plus `postData` — url-encoded form data as a single string, not JSON |
| `sessions.destroy` | An indexer is removed, Deluno shuts down (`DestroyAllSessionsAsync`), or reconciliation finds a session for a host no indexer references any more (`ReconcileSessionsAsync`) | `session` |

Deluno also sends `sessions.list` — with no other fields — from the proxy reachability health check, purely to confirm the helper answers `status: "ok"`; it reads and stores nothing from the response beyond that.

The `session` name Deluno uses is always `"deluno-" + host` (one helper session per site, keyed by host so #749's reconciliation can compare against a plain host list). `maxTimeout` is the person's configured **Solve timeout** (1-180 seconds, clamped), sent in **milliseconds**. `proxy`, when present, is `{ "url": "...", "username": "...", "password": "..." }` (username/password omitted when the proxy has none) — this is the helper's own request-level proxy, which makes the *helper* route that one request through it; it is unrelated to how Deluno itself reaches the helper.

### Response fields Deluno reads

From every `request.get`/`request.post` answer:

- `status` — checked for exactly `"ok"` (case-insensitive); anything else is treated as failure.
- `message` — only inspected on a non-`"ok"` response, and only to detect the word "session" (case-insensitive), which Deluno takes to mean the helper lost its sessions (e.g. it restarted) and recreates the session once before giving up.
- `solution.url` — the final URL after any redirect; falls back to the URL requested if missing or unparsable.
- `solution.status` — the HTTP status the fetched page actually returned.
- `solution.response` — the page HTML.
- `solution.userAgent` — recorded beside the page, because a Cloudflare clearance cookie only holds with the User-Agent that earned it; any later request replaying these cookies must use the same one.
- `solution.cookies[].name`, `.value`, `.domain` (defaults to the response URL's host if blank), `.path` (defaults to `/` if blank) — everything else FlareSolverr/Byparr put on each cookie (`expiry`, `httpOnly`, `secure`, `sameSite`) is ignored; Deluno does not need it to replay the cookie on a later direct request.

From `sessions.list` (the health check only): `status`, checked for `"ok"`. Nothing else.

### Where FlareSolverr and Byparr diverge

Byparr advertises itself as a drop-in replacement and answers the same endpoint shape, but reading its source (`src/models.py`, `LinkRequest`) turns up two real gaps rather than a faithful reimplementation:

- **`cmd` is accepted but not dispatched on.** Byparr's own field comment says it plainly: *"currently only supports GET requests. This string is purely for compatibility with FlareSolverr."* Every `POST /v1` — regardless of `cmd` — navigates to `url` with a `goto` (a GET). `sessions.create` and `sessions.destroy` are answered as if they were ordinary page fetches (Byparr has no session store at all — the concept doesn't exist), and `postData` sent with `request.post` is accepted in the JSON body but never read or applied, so a tracker whose Cloudflare challenge is only solved by posting form data will not work through Byparr. This is invisible from Deluno's side because Byparr still answers `status: "ok"` — the request "succeeds," it just silently did a GET instead of the POST Deluno asked for.
- **No per-request `proxy` field.** Byparr's `LinkRequest` model has no `proxy` key; its only proxy support is a server-level environment variable (`PROXY_SERVER`/`PROXY_USERNAME`/`PROXY_PASSWORD` in `src/consts.py`) applied to every request the helper makes. Deluno's per-indexer proxy (#748/#749) is silently ignored by Byparr rather than rejected — FlareSolverr is the only one of the two that actually routes a request through the `proxy` Deluno sends it.

Both gaps are Byparr limitations, not bugs in Deluno's client: Deluno sends the same request either way, and both helpers report back a normal-looking `"ok"` response, so there is nothing in the protocol itself that would let Deluno tell them apart. Someone relying on POST-only Cloudflare challenges or per-indexer proxies with a helper should use FlareSolverr.

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
- a processor that stops making progress is handled by the library's stall rule; late processed output is still imported
- import preview uses the same destination resolver as the app UI

## Design Rule

Deluno remains the source of truth for media management decisions.

External tools can observe, summarize, coordinate, and request actions, but they should not duplicate Deluno's routing, import, quality, or metadata decision engines.
