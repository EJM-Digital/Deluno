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

- `GET /health` and `GET /api/health/live`: liveness, anonymous; answers 200 whenever the process is up
- `GET /api/health/ready`: readiness, anonymous; 200 with the check results when every readiness check passes, 503 with the same body when one fails
- `GET /api/system/logs`
- `GET /api/system/jobs`
- `GET /api/system/health` and `GET /api/system/health/summary`: the one health-check list every surface reads from (System → Health's full list, the Dashboard tile, and the bell all call the same endpoint and see the same rows). `POST /api/system/health/{id}/snooze` (`{ hours? }`, default 24, capped at 30 days) and `POST /api/system/health/{id}/unsnooze` manage a check's snooze; a snooze always names its own end, never forever.
- `GET /api/system/lan-access`: whether other devices on the network can reach Deluno (`open`, `blocked`, or `notChecked` on a host with no firewall for Deluno to manage)
- `POST /api/indexers` and `POST /api/download-clients` take `routeToLibraries` (default true, #1135): the server switches a new indexer on for the libraries of its media types and routes a new client to each library of a type it serves that has no client yet, however it was created (drawer, API, installer connect, import). The drawer's opt-out sends false.
- `GET /api/libraries/{libraryId}/folder-chain`: the library's folders checked end to end. For each routed download client it shows the category Deluno sends, whether the client has it, the folder the client really saves it to (read from the client, through path mappings), and whether that is the library's downloads folder. Each client also has an `incomplete` link (#1164): where it keeps a download while it is in progress (`incompleteFolder` on the client), flagged `problem` when it equals or sits inside the folder the download finishes in or the library's downloads folder. Then come the downloads folder, the processor's ready folder when a processor is used, and the library root. Each link is `ok`, `problem` or `unknown`, with a message that says what to change.
- `GET /api/movies/{id}/libraries/`: the libraries a film is in. `notWantedHere` is true for a library whose copy was deleted while another library still manages the film: Deluno stopped wanting it in that library only (#1065). `POST /api/movies/{id}/libraries/{libraryId}/want-again` switches that library back on and makes it Missing and searchable. Watching the whole title again does the same for every library.
- `POST /api/trackers/cloudflare-helper/test` with `{ url }`: whether a FlareSolverr-compatible Cloudflare helper answers at that address. The address itself is the `cloudflareHelperUrl` platform setting, and the tracker engine uses it only for a tracker that answers with a Cloudflare check.
- `GET /api/system/bundled-components`: one row per app (`kind`, `displayName`, `category`, `installed`, `reachable`, `defaultHost`, `defaultPort`, `connected`, `needsAccount`, `accountExists`). `connected` is true when a Deluno connection to that app on this computer and port exists. `needsAccount` is true only for `weir` when it answers and has no account yet; `accountExists` is true only for `weir` when it answers and already has the person's account. The row never carries a password or a stored-login flag.
- `POST /api/system/bundled-components/{kind}/connect` (Write): the person-started Connect button for an installed app, with an optional body `{ "username", "password" }`. It answers `{ kind, name, healthy, message, needsSignIn, protocol, host, port, needsAccount }`. For a download client (`qbittorrent`, `transmission`, `deluge`, `sabnzbd`, `nzbget`) Deluno connects server-side with the installer's stored qBittorrent login or the app's shipped login, and never saves a connection whose test failed: it answers `needsSignIn: true` with `protocol`, `host` and `port` so the screen opens the connection form. For `weir` the body is required: if Weir has no account yet Deluno creates it with those values, otherwise it signs in with them, then makes Deluno's API key (read, imports), registers Deluno in Weir and adds Weir as the processor. The username and password live in memory for that call only and are never stored, logged or written to Activity. Wrong password: `200 { healthy: false, message: "Weir did not accept that username and password." }`. An unknown kind is 404.
- `POST /api/system/bundled-components/connect-all` (Write): `{ results: [ ...one connect answer per installed, not yet connected app ] }`. Weir is never connected here; when it is installed and not connected its entry says `needsAccount: true`.
- `GET /api/companion-tools`: each tool carries an optional `url`, where it answers on this computer, for an "Open" link. `POST /api/companion-tools/install` only installs; connecting goes through the routes above. No route returns an installed app's password to anyone: the stored-login route is gone and stored logins are deleted on start (#1061, #968).
- `POST /api/filesystem/libraries/{libraryId}/files-gone`: the answer "These files really are gone" to the unreachable-library note. A library whose folder looks unreachable (empty, or a share that dropped out) changes nothing and raises one clearable note; this call confirms a real mass delete and lets the check mark the missing files (#1070).
- `POST /api/system/lan-access/allow`: adds Deluno's Windows Firewall rule for its port on every network profile. If Deluno lacks administrator rights, Windows asks for permission on the computer it runs on.
- `GET /api/monitoring/dashboard`
- `GET /api/monitoring/alerts`
- `GET /api/monitoring/diagnostics?query=&category=&severity=&sinceUtc=&pageSize=&pageToken=`
- `GET /api/monitoring/export/prometheus`
- `GET /api/monitoring/export/influx`
- `GET /api/dashboard/summary`: every Dashboard panel in one cached response (see "The Dashboard summary" below).
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
- `ActivityEventAdded` (`{ id, message, category, label, severity, createdUtc }`; `label` is the plain-words name of the event kind, see below)
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
- `EpisodeChanged`
- `IndexerProxyChanged`
- `DashboardSummaryUpdated` and the Live events below (#1097): pushed with their data, never a nudge.
- `DashboardSummaryChanged` is no longer sent (it was a nudge with a 2 second gap); the type stays in the web for old subscribers.

Current gap:

- entity-change events carry only `{ id }`; consumers refetch state rather than applying a second serialized copy
- import/recovery and wanted-state coverage is still incomplete and should not be assumed to be authoritative everywhere
- monitoring export endpoints are intentionally unauthenticated only inside Deluno's authenticated API boundary (no public scrape endpoint)

### Live: pushed changes (#1097)

Live is fully live: the server sends each change itself the moment it is saved, and the page applies it with no fetch after it. Every push is a `RealtimeEvent` envelope `{ seq, name, subject, at, data }`. Sequence numbers are issued once per event, in order, and a reconnect asks for what it missed (`Resume`); the web drops any sequence it has already applied, so nothing is counted twice.

**The "someone is watching" signal.** A screen that shows Live or Transfers joins the `live` subject (`RealtimeGroups.Live`, valid in `Subscribe`). The server counts the connections in each subject (`IRealtimeWatchers`; joining twice from one connection is one watcher; a dropped connection leaves everything it had joined). While at least one connection is in `live` AND something is downloading, the download clients are asked every 1 second (`DownloadProgressPublisher` and `DownloadTelemetryRefreshService`), each reading pushes `DownloadProgress` (when an item changed) and a `DownloadSpeedSample`. With nobody watching, or nothing downloading, the old intervals apply (2 s while active, 20 s idle), so there is no cost when nobody looks. Nothing for the Dashboard is built while no connection is in `dashboard` or `live`.

**Summary sections, pushed.** `DashboardSummaryUpdated` on the `dashboard` subject: `{ asOfUtc, sections, unavailable }`. `sections` holds whole summary sections (named as in `GET /api/dashboard/summary`: `library`, `downloads`, `pipeline`, `imports`, `automation`, `indexers`, `processors`, `retention`, `subtitles`, `requests`, `blocklist`, `health`, `storage`, `system`, `activity`), each exactly the shape the REST summary returns, each with its own `asOfUtc`. `unavailable` names sections that could not be built this time; the page keeps what it had. A change marks the sections it can move; the changes of a burst are held for at most 100 ms and sent as one push of the union, so a change reaches the screen in about 100 ms plus the time to build the section. The web writes a pushed section into the cached summary (`["dashboard","summary"]`) only when it is at least as new as what is cached, so a replay never overwrites newer data.

**Events on the `live` subject** (all payloads camelCase; timestamps ISO 8601; the server sends them only after the change is saved):

| Event | Sent when | Payload |
| --- | --- | --- |
| `JourneyStageChanged` | A download moves on its journey: grab sent or refused, first seen in the client, finished in the client, import done or failed, never shown, archived, client-reported failure, retry asked | `{ dispatchId, releaseName, mediaType, entityType, entityId, libraryId, stage, detail, at }`. `stage` is the timeline word (`grab_succeeded`, `grab_failed`, `detection_succeeded`, `client_completed`, `import_succeeded`, `import_failed`, `never_detected`, `archived`, `webhook-completed`, `webhook-failed`, `client-reported-*`, `manual_retry_requested`); `detail` is the reason when there is one |
| `ProcessorHandoffChanged` | A hand-off to the processor is created, changes status, or the processor reports a new state | `{ handoffId, queueItemId, libraryId, releaseName, state, processorName, message, at }`. `state` is the hand-off status (`waiting`, `submitted`, `accepted`, `started`, `completed`, `failed`, `rejected`, `timed-out`, `stalled`, `lost`, `cancelled`, `released`) or the processor's own state (`queued`, `scheduled`, `working`, `completed`, `passed-through`, `failed`) |
| `RecoveryCaseChanged` | An import recovery case opens (a repeat of the same failure is not a new case) or closes (resolved, dismissed or settled) | `{ caseId, state: "opened" \| "closed", kind, title, entityType: "movie" \| "series", entityId, at }`; `kind` and `title` are null when closed |
| `JobChanged` | A background job is added, starts, is handed back to wait, finishes, fails and will be tried again, or fails for good | `{ jobId, kind, state: "queued" | "started" | "waiting" | "finished"\| "finished" \| "retrying" \| "failed", title, libraryId, message, at }`; `title` is the plain-words name of the kind of job |
| `SearchCycleProgress` | A library search finishes each title, and when the cycle ends | `{ libraryId, libraryName, mediaType, state: "running" \| "finished", checked, planned, at }` |
| `IndexerQueryRecorded` | An indexer is asked (`phase: "asked"`) and when it answers (`phase: "answered"`) | `{ indexerId, indexerName, phase, query, resultCount, elapsedMs, failed, failure, at }`; `resultCount` and `elapsedMs` are null while `asked`; `failure` is the plain reason when `failed` |
| `ClientStateChanged` | A download client turns unreachable or reachable, or is paused or resumed (seen on the client reading) | `{ clientId, clientName, reachable, paused, message, at }` |
| `DriveSpaceChanged` | A file was imported: the drive it landed on, with its free space | `{ path, freeBytes, totalBytes, at }` |
| `DownloadSpeedSample` | Every reading of the download clients while a screen watches | `{ downloadMbps, uploadMbps, active, at }` (MB/s; `active` is the count downloading) |
| `ReleasesTurnedDown` | A search looked at releases and did not take some of them (once per search with any; a preview search sends nothing) | `{ title, entityType, entityId, atUtc, items: [{ release, group, label }] }`; `group` is `language`, `notBetter`, `tooBig`, `fewSeeders`, `fake` or `other`, and `label` is its plain wording. A release is once per search, by the first thing that turned it down |
| `ConnectionUsed` | Deluno handed a title to a processor (and again as it finishes), a media server answered a refresh, a Seerr request was received, a subtitle was fetched, or an import list was checked | `{ kind, id, name, countToday, busy, atUtc }`; `kind` is `processor`, `mediaServer`, `requests`, `subtitles` or `lists`; `countToday` includes this use (for `lists` it is the lists that provider is watching); `busy` is a processor's titles right now |
| `ConnectionChecked` | A real test of one connection finished (the regular check, Test all, or a per-connection Test button). Also sent on the `dashboard` subject | `{ kind, id, name, ok, slow, message, checkedUtc }`; `kind` is `indexer`, `downloadClient`, `processor`, `mediaServer`, `requests`, `metadata` or `lists`; `slow` is only ever true for a connection that answered |

The existing `DownloadProgress`, `QueueItemAdded`, `QueueItemRemoved`, `QueueItemStatusChanged`, `ImportStateChanged`, `Dispatch*`, `ActivityEventAdded` and `HealthChanged` events keep their subjects and payloads.

`GET /api/dashboard/live-extras` also carries (#1098): `turnedDown { total, reasons[{ group, label, count }], latest[{ release, group, label, title, entityType, entityId, atUtc }] }` (every group listed in the order `language`, `notBetter`, `tooBig`, `fewSeeders`, `fake`, `other`, zeros included; `latest` is the newest 20 since the person's midnight), `cleanedUpToday` (finished torrents removed from a client after they had shared enough) and `connections[{ kind, id, name, enabled, healthy, countToday, busy }]` (processors, media servers, Seerr, subtitle providers and one entry per import-list provider family). Download telemetry carries `summary.seedingCount` per client and each queue item's `etaSeconds` (the client's own time-left reading; 0 when it gives none). Indexers already carry `queryLimit`, `limitsUnit` and `queryLimitStatus { limit, count, resetsAtUtc }`.

**Web: the hook API** (`apps/web/src/lib/use-signalr.tsx`, `realtime-state.ts`, `realtime-cache.ts`, payload types in `lib/api/types/live-events.ts`, exported from `lib/api/types` and re-exported by `use-signalr`):

- `useLiveEvent(name, handler)`: subscribes to one pushed event on the subject the server sends it on, and also joins `live`, which is what makes the server read the clients every second. The handler is stable across renders; `name` is any key of the live event table above plus the existing queue, activity, health and summary events.
- `useLiveWatching()`: joins `live` without listening for anything. Transfers (the queue screen) calls it; Live calls it through its own `useLiveEvent`s.
- `useRealtimeConnected()`: true while the connection is up. `isRealtimeConnected()` is the same flag outside React; `setRealtimeConnected` is the provider's.
- `usePollInterval(ms)`: `ms` while the connection is down, `false` (no timer) while it is up. Use it for any query that sets its own `refetchInterval`, and to show "updated N s ago" only while it returns a number.
- `useRouteSources` applies the same rule for every source: a source's `refetchInterval` is a safety net that does not run while the connection is up and returns the moment it drops.
- A pushed `DashboardSummaryUpdated` is written into the React Query cache under `["dashboard","summary"]` (`DASHBOARD_SUMMARY_KEY`) before any subscriber runs, so a screen that reads the summary query is already current: do not refetch after it.

Measured: the coalescing window is 100 ms and a test asserts a pushed section is sent within 500 ms of the change on a fake clock; about 1 s for download progress while watched. CPU and network cost while watching is measured on the rig and recorded on #1097.

### The Dashboard summary

`GET /api/dashboard/summary` (Read) is the one request behind the Dashboard (#1082). It returns every panel's at-a-glance numbers in a single compact JSON document, so the screen asks for one thing instead of two dozen. Deluno's own rule for it (#1081): it is built only from stored rows and in-memory readings. It never calls a download client, indexer, media server or processor, never probes the internet, and never sizes a disk or walks a folder while a person waits.

How it stays quick:

- Every section is built at the same time, each with its own scope and a 3 second budget. A section whose source throws or runs long is `null` and named in `unavailableSections`; every other section is unaffected. An empty install answers with plain zeros, empty lists and `null` for things never measured; it is never an error.
- The whole answer is remembered for 4 seconds and built once (single flight) however many screens ask at the same moment. Typical cost: about 30 ms building on 3,000 films, 300 shows and 10,000 queued jobs, and a few milliseconds when remembered.
- Each section carries its own `asOfUtc`, because a cached reading is older than the response around it. The response is sent with `Cache-Control: no-store`.
- Anything that would otherwise need a live call reads the last reading instead: the download queue and client rows come from the last reading the progress publisher took (`downloads.hasReading` is false until there is one, and the counts then mean "not measured", not "idle"); disk sizes come from a background reading refreshed about once a minute (`storage.readingUtc` is `null` before the first); the internet flag is the connectivity monitor's last verdict; leftover downloads are the last leftover scan's answer (`downloads.leftovers.scanned` is false until a scan has run); the automatic-search pause gate is decided from the last download reading.

Shape (every field is camelCase; a trailing `?` below means "may be `null`"):

```
{
  asOfUtc, buildMilliseconds, unavailableSections: string[],
  library?:    { asOfUtc, movies: Counts, shows: Counts, totalBytes, libraries: [{ id, name, mediaType, bytes }],
                 mix: { movieQuality, movieCodec, episodeQuality, showCodec: [{ label, count }] }  // biggest first, at most 8 (the tail folded into "Other"), "Unknown" for unrecorded,
                 collections: { followed, monitored, members, inLibrary, syncFailing, lastSyncedUtc? } },
                 Counts = { total, monitored, unmonitored, missing, upgradable, qualityMet, upcoming,
                            downloading, airing, fileBytes }   // the catalogue shelf's own facet counts
  downloads?:  { asOfUtc, hasReading, readingUtc?, active, queued, stalled, processing, importReady,
                 waitingForProcessor, downloadMbps, uploadMbps,
                 clients: [{ id, name, protocol, reachable, paused?, version?, warnings, freeSpaceBytes?,
                             healthStatus, active, queued }],
                 sharingHolds, sharingExtraBytes,
                 leftovers: { scanned, count, bytes, scannedUtc? }, failedDownloads },
  pipeline?:   { asOfUtc, today: { grabbed, detected, imported, failed },
                 totals: { grabbed, detected, imported, failedGrabs, failedImports, activeDispatches },
                 averageGrabToDetectionSeconds?, averageDetectionToImportSeconds? },
  imports?:    { asOfUtc, recoveryOpen, recoveryByKind: { [kind]: n }, failedImportJobs, failedProcessorHandoffs },
  automation?: { asOfUtc, autoStartJobs,
                 queuePause: { enabled, threshold, moviesPaused, tvPaused, moviesWaiting, tvWaiting,
                               unreadable, unreadableReason? },
                 libraries: [{ libraryId, name, mediaType, status, nextSearchUtc?, nextMissingSearchUtc?,
                               nextUpgradeSearchUtc?, lastCompletedUtc?, lastError? }],
                 lastSearchCycle?: { libraryId, libraryName, mediaType, status, planned, queued, skipped,
                                     startedUtc, completedUtc? },
                 tasks: [{ key, name, lastResult, lastCompletedUtc?, nextRunUtc? }] },
  indexers?:   { asOfUtc, total, healthy, failing, rateLimited, disabled, queriesToday, failedQueriesToday,
                 grabsToday, topFailing: [{ id, name, reason }] },
  processors?: { asOfUtc, total, healthy, failing, handoffsToday, waiting },
  retention?:  { asOfUtc, leavingSoon, nextLeaving: [{ entityId, title, mediaType, removeAfterUtc }] (3),
                 readyToRemove, removedLast7Days, removedBytesLast7Days },
  subtitles?:  { asOfUtc, wanted, held },
  requests?:   { asOfUtc, seerrServers, seerrHealthy, seerrFailing,
                 acquisitionsLast7Days: { [status]: n }, importLists, importListsByLastSync: { [status]: n },
                 listTitlesAddedLast7Days },
  blocklist?:  { asOfUtc, count },
  health?:     { asOfUtc, problems, unknown, suggestions, lastRunUtc?,
                 topProblems: [{ id, title, category, message, sinceUtc?, link, snoozed }] (30) },
  storage?:    { asOfUtc, readingUtc?,
                 volumes: [{ id, label, roles: ["data"|"library"|"downloads"|"processor"], paths, available,
                             totalBytes?, freeBytes? }] },
  system?:     { asOfUtc, version, updateAvailable, latestVersion?, uptimeSeconds,
                 machine?: { capturedUtc, cpuPercent, memoryBytes, memoryPercent? },
                 jobs: { running, queued, failed },
                 lanes: [{ name, state: "running"|"idle"|"failing"|"restarting"|"disabled", activeJobs, lastError? }],
                 lastBackupUtc?, nextBackupUtc?, backupsEnabled, internetOutage, outageSinceUtc? },
  activity?:   { asOfUtc, windowHours, searchesRun, downloadsStarted, importsCompleted,
                 cleanupRemovals, retentionRemovals,
                 recent: [{ id, category, label, message, relatedEntityType?, relatedEntityId?, createdUtc }] (8, newest first, label = plain-words name of the kind, #1086; category is the raw code) }
                 // counts are the last 24 hours; the stream itself stays GET /api/activity; the plain label comes from the shared activity mapping (#1086)
}
```

Notes on the numbers: `library.mix` counts files on disk (a show's codec counts shows, its quality counts episodes); `subtitles.held` is files in libraries that ask for subtitles minus `wanted`, so it reads a little low where titles carry their own language override. Not in the summary because no stored number exists (they need a live call, which the summary never makes): Seerr's own pending requests and what a media server is playing right now. `library.*.total` counts every title; every other library count is over monitored titles, each title under exactly one rung, exactly as the shelf's chips count them. `fileBytes` and `libraries[].bytes` are the sum of the tracked file sizes Deluno already stores (no disk is read). `pipeline.today` is since local midnight on the machine running Deluno. `retention.removedLast7Days` and `...Bytes` add up every rule's confirmed and checked runs (previews removed nothing). `subtitles.wanted` is count-only and counts a title with its own language override as wanted, so it can read a little high for a library that uses overrides. `health.topProblems[].link` is the web path of the screen that clears that kind of problem (System for anything without a better home).

Realtime: the hub pushes the changed summary sections themselves (`DashboardSummaryUpdated`, within about 100 ms; see "Live: pushed changes" above) whenever a source the summary reads has changed (health, queue items, download progress, import state, search runs, activity, dispatch stages, and the automation, movie, series, episode, library, indexer, indexer proxy, download client, import list and settings change events). The web writes them into its cache and does not fetch after them; `GET /api/dashboard/summary` is the first load and the safety net while the connection is down.

## Movies

Implemented endpoints:

- `GET /api/movies/import-recovery?limit=n&includeDetails=true` — the counts cover every open case; the list is the newest 50 (`limit` up to 500, 0 for counts only) without `detailsJson` unless asked
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
- `GET /api/movies/page` — the paged catalogue list; see below. `facets=false` leaves the quick-filter counts out and answers only `totalCount`; the counts are kept for ten seconds and dropped the moment the catalogue is written to
- `POST /api/movies/metadata/jobs` — `{ forceAll?, take? }` → `{ enqueuedCount, remainingCount, staleCount, markedForRefreshCount, message }`
- `POST /api/movies`
- `POST /api/movies/bulk`
- `DELETE /api/movies/bulk`
- `POST /api/movies/bulk/quality-profile`
- `POST /api/movies/bulk/tags`
- `POST /api/movies/bulk/search`
- `POST /api/movies/bulk/reassign-library`
- `POST /api/movies/bulk/rename-preview`

Current UI contract expectations:

- library rows should treat `GET /api/movies/page` as the source of truth for catalog state
- wanted and import-recovery are first-class operational views, not hidden utilities
- monitoring, search, metadata, and bulk actions are already part of the live product surface
- metadata remaps are two-step operations: the UI must show the preview and submit its confirmation token; a missing/stale token or a newly claimed identity is rejected without changing the title

Current gaps:

- the paged catalogue contract includes SQL-backed filtering, sorting, total count, and facets; callers must follow `nextPageToken` rather than request the whole catalogue

## Series

Implemented endpoints:

- `GET /api/series/import-recovery?limit=n&includeDetails=true` — as the movie twin
- `GET /api/series/wanted`
- `GET /api/series/{id}/inventory`
- `GET /api/series/search-history`
- `POST /api/series/import-recovery`
- `DELETE /api/series/import-recovery/{id}`
- `GET /api/series/{id}`
- `GET /api/series/{id}/numbering` — also returns which orderings TMDb's episode-groups refresh actually found for this title (`availableNumberingSchemes`) and any other ordering TMDb offers that Deluno does not resolve episodes by yet (`otherNumberingSchemeNames`) (#863)
- `PUT /api/series/{id}/numbering` — selects standard/daily/anime and standard/air-date/absolute/dvd/scene keys; explicit owner mappings are protected from provider refreshes
- `PUT /api/series/monitoring`
- `PUT /api/series/episodes/monitoring`
- `DELETE /api/series/episodes/{episodeId}/file` — moves that episode's one file to the recycle bin (never off the disk for good); the episode shows missing and a restore puts the file back and links it again (#1188). A film's `DELETE /api/movies/{id}/files` does the same
- `POST /api/series/{id}/search`
- `POST /api/series/{id}/metadata/refresh`
- `POST /api/series/{id}/metadata/link/preview` — adds proposed season/episode counts and the number of existing episodes absent from the proposed provider catalogue
- `POST /api/series/{id}/metadata/link`
- `PUT /api/series/{id}/metadata/override`
- `GET /api/series/page` — the paged catalogue list; see below. `facets=false` behaves as on the movie twin
- `POST /api/series/metadata/jobs` — same shape as the movie twin
- `POST /api/series/{id}/episodes/search` — each selected episode resolves its own installed path, size, current/target quality, and same-library immutable-plan evaluation before candidate comparison; replacement authority is bound to that exact path It takes `mode`: `send` (default) searches and sends the best release once, listing nothing to choose from, and never sends a second release for an episode already downloading; `choose` sends nothing and returns the compared releases (#1175).
- `POST /api/series/{id}/grab` — a hand send for a title or episode that already has a download on its way is refused with 409 `already-downloading` (client name and question) unless the body carries `duplicateAction` of `replace` (removes the first download from its client) or `send-as-well`; `/api/movies/{id}/grab` behaves the same (#1175)
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
- `GET|PUT /api/quality-model`
- `GET|POST|PUT|DELETE /api/tags`
- `GET|POST|PUT|DELETE /api/auto-tag-rules` (#1024: rules are tags plus conditions; same-kind conditions are OR-ed, different kinds AND-ed, each negatable; `removeAutomatically` takes the tags back)
- `POST /api/auto-tag-rules/run` (#1024: runs every rule over the titles already in the library, on request only)
- `GET|POST|PUT|DELETE /api/intake-sources`
- `POST /api/intake-sources/{id}/sync`
- `GET /api/intake-sources/{id}/diagnostics?pageSize=n&pageToken=…`
- `GET|POST|PUT|DELETE /api/custom-formats`
- `POST /api/custom-formats/dry-run`
- `GET|POST|PUT|DELETE /api/destination-rules`
- `GET|POST|PUT|DELETE /api/policy-sets`
- `GET /api/media-plan-scenarios`
- `GET /api/media-plan-scenarios/{id}/compile?mediaType=movies|tv`
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


Current UI contract expectations:

- TV remaps are blocked when another held show owns the proposed identity or when the proposed provider catalogue omits any existing episode row; Deluno does not silently merge shows or mix episode catalogues
- an accepted, reviewed remap refreshes provider-owned identity/catalogue fields while keeping files, monitoring, history, tags, numbering overrides, and plan assignments

- quality profiles, tags, intake sources, custom formats, destination rules, policy sets, and saved library views are all active configuration concepts
- platform settings now include `searchScoringMode` (`hybrid`, `rules-only`, `ml-only`) so users can explicitly choose deterministic ranking, model-priority ranking, or both
- intake sources now carry per-source filter/routing fields (`requiredGenres`, `minimumRating`, `minimumYear`, `maximumAgeDays`, `allowedCertifications`, `audience`) plus sync diagnostics (`lastSyncUtc`, `lastSyncStatus`, `lastSyncSummary`)
- the quality model endpoint exposes explicit editable tiers with movie/episode size bounds and upgrade-stop policy
- custom format dry-run is implemented and should be documented as a real workflow, not a future one
- custom-format names are unique within a normalized media type; an accidental
  duplicate is rejected with an edit-the-existing-rule message, while Movies
  and TV may deliberately own separate rules with the same name
- every selected custom format with a valid matcher compiles to a plan-scoped
  matched/not-matched decision. The profile's words (must not have, avoid, do
  not care, prefer, strongly prefer) control the decision; imported numeric
  scores remain provenance only. The immutable source records the saved rule
  id/name, matcher, intent, and version
- migration preview/apply exists and should remain tied to authenticated single-user setup and import workflows

Current gaps:

- the platform route file is carrying too many responsibilities; contract clarity is now stronger than implementation separation
- several newer docs describe presets and advanced settings, but the route registration is still concentrated in a single large endpoint file

## Search Scoring And Explainability

Implemented endpoints:

- `GET /api/ranking-model/status`
- `POST /api/ranking-model/train`
- `POST /api/ranking-model/rollback`
- `GET /api/intelligent-routing/snapshot`
- `GET /api/intelligent-routing/anomalies`
- `POST /api/intelligent-routing/recommend-release`

Current UI contract expectations:

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
- `PUT /api/libraries/{id}/quality-profile`: `{ qualityProfileId }` gives the library another quality profile (one made for its kind of media) and re-checks every title that follows it (#1169). `PUT /api/quality-profiles/{id}` and a title's own profile do the same for what they judge.
- `PUT /api/libraries/{id}/workflow`
- `POST /api/libraries/{id}/search-now`
- `POST /api/libraries/{id}/skip-cycle`
- `GET /api/libraries/{id}/import-existing/preview?cursor=...&take=50` — reads a bounded page of existing files/folders without writing
- `POST /api/libraries/{id}/import-existing/selected` — imports only the explicitly selected preview paths (maximum 100 per request)
- `POST /api/libraries/{id}/import-existing` — starts a run, returns `202` with its progress
- `GET /api/libraries/{id}/import-existing` — the run in flight, or the most recent one
- `POST /api/libraries/{id}/import-existing/pause`
- `POST /api/libraries/{id}/import-existing/resume`
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

Multi-file TV import is deliberately all-or-nothing. A directory must identify one
season, every video must map to one or more episodes in the selected show's persisted
catalogue, and no episode or destination may be claimed by two files. Deluno places no
file when that review fails. Once approved, filesystem placement is compensated if the
single catalogue transaction fails: copied or linked destinations are removed and moved
sources are restored. A retry is reported as already committed only when every episode
still owns its reviewed path and the source and destination SHA-256 content match; file
size alone is not accepted as proof.

When an import belongs to a download dispatch, completion appends
`file_provenance_recorded` to that dispatch's timeline. Its payload correlates
the exact original release title, indexer, client and client item, processor,
source and destination paths, rename, transfer/fallback result, destination
rule, matched custom rules, cleanup outcome, media probe, and immutable
preference-plan id/version/hash. The Movies and Series preference snapshots
also store `releaseNameEvidence` independently of `filePath`, so a clean library
name cannot erase NF/AMZN/group/edition/repack evidence and a later media probe
re-evaluates the same immutable matchers against that preserved title.

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
- `POST /api/download-clients/{clientId}/resume` — resumes a client that is paused as a whole (the `paused` flag on the Dashboard summary's client rows and `ClientPaused` in the telemetry snapshot), not one item in its queue. Implemented for SABnzbd (`mode=resume`) and NZBGet (`resumedownload`, `resumepost`, `resumescan`); every other client answers 400 with the reason. Authenticated. On success it writes a plain-words Activity row (`download.client.resumed`), refreshes the stored telemetry snapshot so the next read shows the client downloading again, and answers 200 with `{ clientId, action: "resume-client", succeeded, message }`.
- `POST /api/download-clients/{clientId}/queue/{queueItemId}/remove` — the Queue's "why" dialog (cleanup sheet J1/J2, #924): body is `{ reason }`, one of `failed` ("It's bad": remove from the client, delete its files, blocklist the release everywhere, search again), `unwanted` ("I don't want it": remove from the client and delete its files, no blocklist), or `forget` ("Just stop tracking it": Deluno forgets the download; the client keeps it and its files untouched). This replaces the old single confirm and the "Allow removing client queue entries" setting that used to gate removal — every reason now acts without a separate on/off gate.
- `GET /api/download-clients/{clientId}/queue/{queueItemId}/cleanup-preview` — the ownership/threshold opinion behind a removal: whether Deluno can prove it owns this client item and its payload before removing them
- `POST /api/download-clients/{clientId}/queue/{queueItemId}/health/{kind}/ignore` — snoozes one download-health finding for that queue item
- `POST /api/download-clients/{clientId}/categories/check` — verifies a library-specific category against SABnzbd or qBittorrent and reports `ready`, `missing`, `unreachable`, `configuration`, or `unsupported` without claiming the route is ready when it cannot be verified

Implemented webhook endpoints:

- `POST /api/download-clients/{clientId}/webhook` (a webhook cannot change which client item a dispatch belongs to; an id already recorded stays). There is no `POST /api/download-clients/{id}/grab` any more: a grab goes through the release grab route, so it is routed, reserved by its hash and recorded like every other grab.
- `GET|POST|PUT|DELETE /api/notification-webhooks`
- `POST /api/notification-webhooks/{id}/test`

Current UI contract expectations:

- enable/disable and update flows are already implemented and should not be described as future-only
- telemetry has both live polling and persisted last-known behavior
- queue actions and manual direct grab are now part of the integration surface
- inbound download-client webhook payloads are normalized and idempotent across duplicate completed/failed callbacks
- outbound notification webhooks support event-filter matching, Discord embed formatting, and bounded retry delivery before recording final error state

Saved secrets that cannot be read here (#1094):

- API keys, passwords, tokens and webhook secrets are protected per computer and account. One saved on another computer (a restored backup, a moved install) cannot be read here. That never fails a list: every reader goes through `ISecretProtector.TryUnprotect`, which answers no value, a value, or "cannot be read here".
- The list items for indexers, download clients, indexer proxies, processor connections (`/api/integrations/processors/connections`), media servers, Seerr servers and subtitle providers carry `secretUnreadable: boolean` and `secretProblem: string | null` (plain words: "The saved API key was stored on another computer and can't be read here. Enter it again."). The secret itself is listed as missing (`hasApiKey`/`hasPassword`/`hasToken` false).
- `GET /api/settings` reports a metadata key stored on another computer as not configured (`metadataTmdbApiKeyConfigured`, `metadataOmdbApiKeyConfigured`, `mdbListApiKeyConfigured` false) and sets `metadataTmdbApiKeyUnreadable`, `metadataOmdbApiKeyUnreadable` or `mdbListApiKeyUnreadable` plus a plain-words `metadataKeyProblem`. Sending the key again with `PATCH /api/settings` clears it. A subtitle provider whose key is unreadable is not asked, and its test and fetches answer with the same sentence.
- While the flag is set and the connection is enabled, `healthStatus` is `degraded` and `lastHealthMessage` is that sentence, so Health, the Dashboard and any background work (tests, searches, grabs, refreshes) report the same clear reason instead of a crypto error. Tests and grabs refuse with it rather than trying the missing key.
- Entering the secret again through the normal save (`PUT`/save) replaces the stored value and clears the flag.

Current gaps:

- reorder/prioritization contracts are still indirect
- deeper per-client history fidelity and outcome tracking remain incomplete

## Blocked Releases

Implemented endpoints:

- `GET /api/blocked-releases`
- `POST /api/blocked-releases` — block a release from wherever it is named (a search result never grabbed, a title's search history, an Activity entry, not only a proposal Deluno itself raised): `{ releaseName, indexerName?, torrentHashOrItemId?, mediaType?, entityId?, title? }`
- `DELETE /api/blocked-releases/{id}` — lifts the block; starts no search (clearing a block never fires a search automatically, only the title page's own "search now" does)
- `POST /api/blocked-releases/{id}/refuse` — answers a proposed ("ask me") entry by refusing it
- `POST /api/blocked-releases/{id}/cleanup` — the manual half of the scheduled clear-out: deletes the payload, removes the client queue entry, and clears the client's memory of the release, without overruling an active seeding/sharing-rule hold

Current behavior:

- a block always matches by the release's identity across every indexer, not only the one it came from: by torrent hash or NZB item id when one is known, and by normalized release name otherwise — never scoped to a single indexer
- blocking, listing, and lifting are offered from every place a release is named (search results, the Queue, a title's own history, Activity, and the Blocklist screen itself), all calling these same routes

## Activity, Queue, And External Operations

Implemented core operational endpoints:

- `GET /api/jobs?pageSize=n&pageToken=…&failed=true&includePayload=true` — newest first; `failed=true` keeps only jobs that need a person (up to `pageSize=500` in one call). A list leaves `payloadJson` out unless `includePayload=true`, and even then leaves out a payload over 64 KB (it can carry a whole torrent file)
- `GET /api/jobs/{id}` — one job with its whole payload
- `GET /api/activity?pageSize=n&pageToken=…&type=&title=&file=&source=&sourceName=&outcome=&reason=&since=&until=&relatedEntityType=&relatedEntityId=` — the Activity history (#1100), newest first and paged; every filter is optional and they combine. `type` is one of `search`, `turned-down`, `grabbed`, `imported`, `subtitles`, `blocklisted`, `removed`, `restored`, `failed`, `health`, `other`; `source` is `indexer`, `client`, `processor` or `connection`; `outcome` is `good` or `problem`; `reason` is a turned-down reason group (`TurnedDownGroups`); `since`/`until` are instants (until is exclusive). Each event also carries `title`, `filePath`, `sourceName`, `reasonGroup`, `type`, `source` and `outcome`. Every turned-down release is one `release.turned-down` event ("Turned down <release> · <reason>"), written where it is turned down; the plain newest-first list (no filter) leaves those out, and asking for the type, a title, a file, a reason or a source name brings them in. The kinds, sources and outcomes are defined once in `ActivityHistory` (`src/Deluno.Contracts/ActivityHistory.cs`) and served by `GET /api/activity/filters`. The Activity screen keeps its filters in the address (`/activity?type=turned-down&since=today&reason=language`, `apps/web/src/lib/activity-history.ts`), so a filtered view is a link. Activity retention (#828) prunes these rows with the rest.
  - every event carries `label` (#1086): the kind of event in plain Australian English, for example `download.vanished.recovered` reads "Found its files after it left the client". Screens show `label` and never the raw `category` code, which stays only as a stable key for grouping and filtering. `GET /api/monitoring/diagnostics` items and the `ActivityEventAdded` live event carry the same `label`, and the film and show History tables read it from this feed. The wording is written once, in `ActivityEventLabels` (`src/Deluno.Contracts/ActivityEventLabels.cs`), and `ActivityEventLabels.For(category)` is what any other endpoint (such as the Dashboard summary) calls; a kind with no label of its own reads as its area ("Download activity"), never as the code. A test (`EveryActivityKindHasPlainWordsTests`) fails if any writer can record a kind that has no label there.
- `GET /api/stats?window=24h|7d|30d`
- `GET /api/decisions?pageSize=n&pageToken=…`
- `GET /api/library-automation?pageSize=n&pageToken=…`
- `GET /api/search-cycles?pageSize=n&pageToken=…`
- `GET /api/search-retry-windows?pageSize=n&pageToken=…`
- `GET /api/download-dispatches?pageSize=n&pageToken=…&includeArchived=true&includeRawJson=true` — `notesJson` and `grabResponseJson` are left out of a list when over 64 KB (a grab used to record its whole search plan, twice) unless `includeRawJson=true`; `GET /api/v1/download-dispatches/{id}` always has them. A new grab stores its notes once, with any list longer than ten entries cut to ten and a `<name>Total` beside it
- `GET /api/v1/download-dispatches?pageSize=n&pageToken=…`
- `GET /api/v1/import-resolutions?pageSize=n&pageToken=…`
- `GET /api/download-health?pageSize=n&pageToken=…`
- `GET /api/integrations/external/manifest`
- `GET /api/integrations/external/health`
- `GET /api/integrations/external/queue`
- `GET /api/integrations/external/activity`
- `POST /api/integrations/external/trigger-refresh`
- `POST /api/command`, `GET /api/command`, `GET /api/command/{id}` (#1019, see docs/external-integration-api.md)
- `GET /api/system/status` (#1020, see docs/external-integration-api.md)
- `GET /api/diskspace`, `GET /api/movies/lookup/imdb?imdbId=` (#1021, see docs/external-integration-api.md)
- `DELETE /api/queue/{clientId}/{queueItemId}?removeFromClient=&blocklist=&skipRedownload=`, `DELETE /api/queue/bulk`, `GET /api/history?eventType=`, `GET /api/wanted/missing`, `GET /api/wanted/cutoff` (#1022, see docs/external-integration-api.md)
- `POST /api/integrations/processors/events` (replies and the 1000-file limit: docs/external-integration-api.md, "Replies and limits", #1023)

`GET /api/stats` (#827) aggregates the same facts the Activity feed already
writes in plain words, over a time window, for an external dashboard widget
(Homepage, Homarr) as well as the Dashboard's own "Live activity" summary.
Single-user, so there is no API key or per-caller scoping — every count is
everything this instance logged in the window. `window` defaults to `24h`
when omitted or unrecognised. Response shape:

```json
{
  "sinceUtc": "2026-09-26T12:00:00Z",
  "generatedUtc": "2026-09-27T12:00:00Z",
  "windowHours": 24,
  "searchesRun": 12,
  "downloadsStarted": 3,
  "importsCompleted": 3,
  "cleanupRemovals": 1,
  "retentionRemovals": 0
}
```

Current UI contract expectations:

- activity and queue are already fed by durable job/activity stores, not just transient UI state
- the Queue shows and acts only on Deluno's own downloads: telemetry, health findings, and queue actions are scoped per download client to Deluno's own dispatches (cleanup sheet row M2, #933), not every item a shared client happens to be running
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

## Connection checks and the bell (#1098)

- `GET /api/connections/checks` returns the latest real check of every connection: `[{ kind, id, name, ok, slow, message, checkedUtc }]`.
- `GET /api/connections/versions` returns the version each connection last reported, from its own checks (#1117): `[{ kind, id, version, latestVersion, updateAvailable, releaseUrl }]`. `latestVersion` is the newest stable release of that project, read in the background at most once a day from its public feed (null when not yet read); `updateAvailable` is true only for a stable build older than it, never for a beta or development build, and `releaseUrl` is set only then. Connections that report no version (uTorrent, a plain Torznab or Newznab indexer) are not listed.
- `POST /api/connections/test-all` tests every connection that is switched on, four at a time, pushing `ConnectionChecked` for each as it finishes; it answers `{ tested, results }`.
- `POST /api/connections/{kind}/{id}/test` tests one by kind (`indexer`, `downloadClient`, `processor`, `mediaServer`, `requests`, `metadata`, `lists`). The existing per-connection test endpoints (`/api/indexers/{id}/test`, `/api/download-clients/{id}/test`, the media server, Seerr and processor ones, `/api/metadata/test`) push the same event.
- The regular check (system task `connections.check`, setting `checkConnectionsRegularly`, on unless turned off) tests anything no real call or earlier check touched in 15 minutes. It only reads. A plain Activity line is written only when a result changes.
- `GET /api/attention/summary` is the one definition of "things need a look" (#1185): `{ count, rows, notices, leftovers, suggestions }`. Each row is `{ id, section (media|deluno), kind, tone, title, text, to, action, count, sinceUtc, data }`, where `data` is the record behind the row (the stuck download, the import case, the job) for the bell's buttons. `count` is the sum of the rows' counts: the number on the bell and the number in the tray's "N things need a look". `notices` are a ready Deluno update and newer connected-app versions: listed in their own group, in no number (an update ready is the blue dot, not "needs a look"). `leftovers` and `suggestions` are noted, never counted. The bell reads this endpoint and adds nothing up; the tray calls the same `IAttentionSummaryService` in-process (`src/Deluno.Worker/Attention/AttentionSummary.cs`), so the two numbers are equal by construction.
- `GET /api/attention/no-release` lists wanted titles whose newest search found nothing acceptable; `POST /api/attention/no-release/{movie|series}/{id}/dismiss` hides one until it stops being that problem.
- `GET /api/attention/held-releases` lists searches that ended "held": a usable release (with its link) that needs a person's yes; Transfers shows it under Needs action with Approve (the grab route with force), Find another (blocklist, then search) and Dismiss (`POST /api/attention/held-releases/{historyId}/dismiss`).
- Health has a `machine` area: `machine:sleep`, `machine:drive:<name>`, `machine:reboot`, `machine:temperature`. The Keep awake button is `PATCH /api/settings { keepComputerAwake: true }`.

## Keeping a struck private torrent seeding (#1240)

A strike rule can move a private (or tagged) torrent to a keep-seeding category instead of removing it. Nothing is set on a new install, and every rule saved before this reads as `remove`.

- A download-health rule carries `privateAction` (`remove`, the default, or `move-and-replace`) and `blocklistWhenMoved` (default true). `move-and-replace` is refused unless the rule's action is Replace, it covers torrents, and the kind is not `dangerous-file` (that is always removed and deleted). At the rule's strike count a private torrent (the client's private flag, or its site private by its tracker definition or the person's override) or one whose site carries a seeding tag is moved; a public one is removed as before. The most specific category wins: a tag's, then the site's, then the download client's. With none set anywhere the torrent is left alone and the rule says so. A refused move changes nothing else (no block, no search, no removal), is retried at every check, and shows on System, Health until it works.
- `GET /api/download-health/keep-seeding` returns `{ clients[{ clientId, name, protocol, canKeepSeeding, reason, category }], indexers[{ indexerId, name, privacy, privateOverride, isPrivate, tags, category }], tagCategories[{ tag, category }], problems[...] }`. `PUT` takes `{ clientCategories: { clientId: category }, indexers: { indexerId: { privateOverride, tags, category } }, tagCategories: { tag: category } }`, refuses a category Deluno uses for its own downloads on that client and any category for a Usenet client, and returns the same view. These tags only choose a category; they are not the routing tags on an indexer.
- `POST /api/download-health/keep-seeding/check` (`{ clientId, category }`) asks the client whether the category is ready (`ready`, `missing`, `unsupported` with the reason, `unreachable`). It never creates a category. `POST /api/download-health/keep-seeding/problems/{key}/clear` clears a move that did not work.
- A site added from a tracker definition keeps the definition's privacy (`private`, `public`, anything else `semi-private`); a hand-added Newznab or Torznab source stays `unknown` (not set).
- The client call per client: qBittorrent `torrents/setCategory` (after `setAutoManagement` off for an auto-managed torrent), Deluge `label.set_torrent`, Transmission `torrent-set` `labels` (the keep-seeding label first, the old category dropped, the rest kept), rTorrent `d.custom1.set`, uTorrent `setprops` `s=label`. Each is checked before and after. Usenet clients are never offered it.
