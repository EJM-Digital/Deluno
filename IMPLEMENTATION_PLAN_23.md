# Implementation Plan: Issue #23 - Real Integration & Download Plumbing

**Issue:** [#23 Real Integration & Download Plumbing](https://github.com/jampat000/Deluno/issues/23)  
**Status:** Planning  
**Effort:** 3-4 weeks  
**Blocker:** Yes

## Current State Analysis

### What Works
- ✅ **DownloadClientGrabService** - successfully sends releases to 6 download client protocols (qBittorrent, SABnzbd, Transmission, Deluge, NZBGet, uTorrent)
- ✅ **Circuit breaker** - pauses grabs after 3 failures per client
- ✅ **DownloadDispatchItem** - tracks dispatch metadata (release name, indexer, client, created time)
- ✅ **DownloadClientTelemetryService** - polls clients for current queue/history state
- ✅ **Activity tracking** - logs job queue events
- ✅ **SearchRetryWindow** - stores retry delays for missing/upgrade searches

### What's Missing
- ❌ **Grab outcome persistence** - currently returned as in-memory DownloadClientGrabResult, not stored
- ❌ **Import resolution history** - no tracking of whether imports succeeded or why they failed
- ❌ **Per-client history** - download_dispatches table lacks grab outcome, download client response, import result
- ❌ **Outcome polling** - telemetry snapshot exists, but we don't match our grabs to actual downloads or imports
- ❌ **Retry integration** - failed grabs don't update SearchRetryWindow with exponential backoff
- ❌ **Cleanup policies** - no mechanism to clean up old/failed grabs from queue
- ❌ **Webhook support** - no push notification infrastructure (polling only)

### Database State

**download_dispatches table (current):**
```sql
id TEXT PRIMARY KEY,
library_id TEXT NOT NULL,
media_type TEXT NOT NULL,
entity_type TEXT NOT NULL,
entity_id TEXT NOT NULL,
release_name TEXT NOT NULL,
indexer_name TEXT NOT NULL,
download_client_id TEXT NOT NULL,
download_client_name TEXT NOT NULL,
status TEXT NOT NULL,              -- "queued", "sent", "downloading", etc.
notes_json TEXT NULL,              -- metadata, but no grab outcome
created_utc TEXT NOT NULL
```

**Missing capabilities:**
- grab_status (succeeded, failed, circuitOpen, etc.)
- grab_response_message (why it failed, if it did)
- grab_attempted_utc (when we sent it)
- download_detected_utc (when we saw it in client queue)
- import_status (undetected, detected, importing, imported, failed)
- import_detected_utc (when we first saw the file)
- import_completed_utc (when import finished)
- import_failure_reason (quality, unmatched, etc.)

---

## Implementation Strategy

### Phase 1: Grab Outcome Persistence + API (Week 1-2)
**Goal:** Grabs are recorded with outcome; API exposes all data for dashboard and external tools.

**Files to create/modify:**
1. **V0003DownloadOutcomeTracking.cs** (new migration)
   - Extend download_dispatches with grab outcome columns
   - Add: grab_status, grab_attempted_utc, grab_response_code, grab_message, grab_failure_code, detected_utc, torrent_hash_or_item_id, downloaded_bytes
   - Add indices for fast querying by status, client, grab time

2. **DownloadDispatchItem.cs** (update contract)
   - Add all grab, detection, and import outcome fields (see API Design section)

3. **DownloadDispatchesRepository.cs** (new)
   - RecordGrabAsync(dispatchId, grabResult, grabResponseJson)
   - UpdateGrabStatusAsync(dispatchId, status, message, failureCode)
   - FindDispatchesByGrabStatusAsync(status, clientId, limit)
   - FindUnresolvedDispatchesAsync(minAgeMinutes, clientId)
   - GetDispatchTimelineAsync(dispatchId)
   - QueryDispatchesAsync(filters, pagination)

4. **DownloadDispatchesController.cs** (new API controller)
   - Implements all endpoints from API Design section
   - GET /api/v1/download-dispatches
   - GET /api/v1/download-dispatches/{id}
   - GET /api/v1/download-dispatches/unresolved
   - GET /api/v1/import-resolutions
   - POST /api/v1/download-dispatches/{id}/retry
   - DELETE /api/v1/download-dispatches/{id}

5. **DownloadClientGrabService.cs** (update)
   - After GrabAsync, call dispatchRepository.RecordGrabAsync with full result
   - Store HTTP response code, failure codes, full response JSON

6. **Tests/DownloadClientGrabPersistenceTests.cs** (new)
   - Verify grab outcome is stored after service call
   - Test circuit breaker state is persisted
   - Test timeline events are recorded
   - Test unresolved dispatch detection

7. **Tests/DownloadDispatchesApiTests.cs** (new)
   - Test all API endpoints for correct response shapes
   - Test filtering, pagination, error codes
   - Verify external tools can consume the data

**Success Criteria:**
- All grab outcomes recorded to DB (success, failed, circuitOpen, etc.)
- Grab timestamp, response code, failure code stored
- Timeline events recorded atomically
- Can query grabs by client, status, time range
- API endpoints return structured data with proper error codes
- External tools (fileflows, bazarr) can reliably consume dispatch/import data
- Tests verify end-to-end flow (grab → service → DB → API)

---

### Phase 2: Import Resolution Tracking (Week 2)
**Goal:** We track what happened to downloaded files from grab to import.

**Files to create/modify:**
1. **V0004ImportResolutionTracking.cs** (new migration)
   - Create import_resolutions table
   - Track: which dispatch → which file → import result → why

2. **ImportResolutionItem.cs** (new contract)
   ```csharp
   record ImportResolutionItem(
       string Id,
       string DispatchId,
       string EntityId,
       string MediaType,
       string? DownloadFilePath,
       string DetectionStatus,           // "undetected", "detected", "importing", "imported", "failed"
       string? FailureReasonCode,        // "quality", "unmatched", "sample", "corrupt", etc.
       string? FailureMessage,
       DateTimeOffset? DetectedUtc,
       DateTimeOffset? ImportedUtc,
       DateTimeOffset CreatedUtc
   );
   ```

3. **ImportResolutionsRepository.cs** (new)
   - RecordImportDetectionAsync(dispatchId, filePath)
   - RecordImportSuccessAsync(dispatchId, entityId)
   - RecordImportFailureAsync(dispatchId, reasonCode, message)
   - GetDispatchResolutionAsync(dispatchId)
   - FindUnresolvedDispatchesAsync(clientId, minAgeHours)

4. **Deluno.Filesystem/ImportPipelineService.cs** (update)
   - After successful import, call importResolutionsRepository.RecordImportSuccessAsync
   - After failure, call RecordImportFailureAsync with reason code

5. **Tests/ImportResolutionTrackingTests.cs** (new)
   - Verify import success recorded
   - Verify failure reasons captured
   - Test query paths for unresolved/failed imports

**Success Criteria:**
- Import outcomes linked to grabs
- Failure reasons captured and queryable
- Can identify unresolved grabs (grabbed but not detected in client)
- Tests verify detection → import → outcome flow

---

### Phase 3: Per-Client History & Polling (Week 2-3)
**Goal:** We actively poll clients to match our grabs with their items and track outcomes.

**Files to create/modify:**
1. **DownloadClientPollingService.cs** (new)
   - PollClientQueueAsync(clientId): fetch current queue from client
   - MatchQueueToDispatchesAsync(clientId, queueItems): link client items to our dispatches
   - RecordDetectionAsync(dispatchId, clientItem) when we find a match
   - RecordCompletionAsync(dispatchId, clientItem) when torrent/file is done

2. **DownloadClientHistoryAdapter.cs** (refactor from TelemetryService)
   - Per-protocol adapter to extract: torrent hash, filename, status, progress, addedTime
   - qBittorrent: parse torrent info hash
   - SABnzbd: parse job ID and status
   - Transmission: parse torrent ID
   - Map client item identifiers back to DownloadDispatch

3. **DownloadPollingWorker.cs** (new background job)
   - Registered in Deluno.Worker
   - Runs on configurable interval (default 5 minutes)
   - For each enabled client: PollClientQueueAsync
   - Updates dispatch detection/completion status
   - Emits SignalR events for UI updates

4. **Tests/DownloadClientPollingTests.cs** (new)
   - Mock client responses
   - Verify dispatch matching logic
   - Test per-client adapters

**Success Criteria:**
- Polling service runs on interval
- Client queue items matched to dispatches by name/hash
- Detection and completion times recorded
- Unmatched items logged for debugging
- Tests cover all client protocols

---

### Phase 4: Retry Integration & Cleanup (Week 3-4)
**Goal:** Failed grabs get retried with exponential backoff; old grabs are cleaned up.

**Files to create/modify:**
1. **DownloadRetryService.cs** (new)
   - For each failed grab: check SearchRetryWindow
   - If retry eligible: UpdateNextEligibleAsync with exponential backoff
   - Queue a new search job for the entity
   - Track retry count per grab

2. **DownloadCleanupPolicy.cs** (new)
   - Define retention: unresolved grabs older than X hours
   - Define action: delete from queue, mark as abandoned, etc.
   - ArchiveOldDispatchesAsync(hoursOld, maxPerRun)

3. **DownloadCleanupWorker.cs** (new)
   - Runs daily
   - Calls ArchiveOldDispatchesAsync
   - Logs what was archived

4. **DownloadDispatchesRepository.cs** (extend)
   - UpdateFailureRetryWindowAsync(dispatchId, nextEligible)
   - ArchiveDispatchAsync(dispatchId, reason)

5. **Tests/DownloadRetryAndCleanupTests.cs** (new)
   - Verify exponential backoff calculation
   - Test cleanup policies
   - Verify old dispatches are archived

**Success Criteria:**
- Failed grabs trigger SearchRetryWindow updates
- Exponential backoff prevents thrashing
- Old, unresolved grabs cleaned up on schedule
- Cleanup is auditable (activity log)
- Tests verify all retry scenarios

---

### Phase 5: Webhook Support (Optional, Post-MVP)
**Defer for now.** Polling is sufficient for MVP. Webhooks would be added in a follow-up phase to skip polling when clients support push notifications.

---

## Data Flow Diagram

```
[Release decision engine]
         ↓
[DownloadClientGrabService.GrabAsync]
         ↓
[Client HTTP API]
         ↓
[GrabResult returned]
         ↓
[DownloadDispatchesRepository.RecordGrabAsync] ← saves grab outcome to DB
         ↓
[DownloadPollingWorker runs on interval]
         ↓
[For each client: PollClientQueueAsync]
         ↓
[Match client items to dispatches]
         ↓
[ImportResolutionsRepository.RecordDetectionAsync] ← saves detection
         ↓
[Filesystem import pipeline runs]
         ↓
[ImportResolutionsRepository.RecordImportSuccessAsync/Failure]
         ↓
[If failed: DownloadRetryService updates retry window]
         ↓
[SearchRetryWindow triggers new search on next eligible time]
         ↓
[Dashboard shows unresolved/failed grabs via HTTP]
         ↓
[Activity feed shows outcome history]
```

---

## Dependencies & Order

**Must do in order:**
1. **Phase 1** (Grab outcome persistence)
   - Nothing depends on this, but it's foundational
   - Makes all subsequent phases possible

2. **Phase 2** (Import resolution tracking)
   - Depends on Phase 1 (needs dispatchId to link)
   - Enables unresolved grab detection

3. **Phase 3** (Per-client polling)
   - Depends on Phase 1 & 2 (updates dispatch/resolution records)
   - Makes unresolved grab detection practical

4. **Phase 4** (Retry & cleanup)
   - Depends on Phase 1, 2, 3
   - Adds automation on top of historical data

---

## Testing Strategy

**Each phase has:**
- Unit tests (mocked repos, services)
- Integration tests (real SQLite DB, transactions)
- Scenario tests (full flow: grab → poll → import → retry)

**Test fixtures:**
- Mock download client responses (qBittorrent, SABnzbd, etc.)
- Sample grabs and imports
- Edge cases: circuit breaker, mismatched items, orphaned grabs

---

## API Design (For Dashboard & External Tools)

**Design Principle:** External tools (fileflows, bazarr, etc.) need deterministic, audit-friendly data. All timestamps are ISO 8601 UTC. Error reasons are structured codes, not text blobs.

### Core Endpoints (Phase 1 Deliverable)

#### GET /api/v1/download-dispatches
Query all dispatches with filtering and pagination.

**Query Parameters:**
- `status`: grabbed, downloading, completed, failed, archived
- `clientId`: filter by download client
- `entityType`: movie, episode
- `entityId`: specific entity ID
- `library_id`: specific library
- `minGrabTime`, `maxGrabTime`: ISO 8601 datetime range
- `pageSize`: 10-100 (default 50)
- `pageToken`: opaque cursor for pagination

**Response (200 OK):**
```json
{
  "dispatches": [
    {
      "id": "dispatch-123",
      "libraryId": "lib-movies",
      "mediaType": "movie",
      "entityType": "movie",
      "entityId": "movie-456",
      "releaseName": "The.Matrix.1999.1080p.BluRay",
      "indexerName": "IPT",
      "downloadClientId": "qbittorrent-1",
      "downloadClientName": "qBittorrent Main",
      "createdUtc": "2026-05-09T14:30:00Z",
      
      "grabStatus": "succeeded",           // NEW: succeeded, failed, circuitOpen, other
      "grabAttemptedUtc": "2026-05-09T14:30:05Z",
      "grabResponseCode": 200,             // HTTP status from client
      "grabMessage": "Added to queue",     // SHORT human text
      "grabFailureCode": null,             // NEW: GRAB_FAILED, CIRCUIT_OPEN, AUTH_FAILED, TIMEOUT, etc.
      
      "detectionStatus": "detected",       // NEW: undetected, detected, importing, imported, failed
      "detectedUtc": "2026-05-09T14:35:00Z",
      "downloadedBytes": 4700000000,       // From client queue snapshot
      
      "importStatus": "imported",          // NEW: undetected, detected, importing, imported, failed
      "importDetectedUtc": "2026-05-09T15:00:00Z",
      "importCompletedUtc": "2026-05-09T15:15:00Z",
      "importedFilePath": "/media/movies/The Matrix (1999).mkv",
      "importFailureCode": null,           // QUALITY_TOO_LOW, UNMATCHED_FILE, SAMPLE, CORRUPT, etc.
      "importFailureMessage": null
    }
  ],
  "nextPageToken": "cursor_abc123...",
  "hasMore": true
}
```

---

#### GET /api/v1/download-dispatches/{dispatchId}
Get full details for a single dispatch.

**Response (200 OK):**
```json
{
  "id": "dispatch-123",
  // ... same as list response ...
  "timeline": [
    {
      "eventType": "created",
      "timestamp": "2026-05-09T14:30:00Z",
      "details": { "reason": "manual_search" }
    },
    {
      "eventType": "grab_attempted",
      "timestamp": "2026-05-09T14:30:05Z",
      "details": {
        "clientResponse": { "code": 200, "message": "Added to queue" },
        "magnetUrl": "magnet:?xt=urn:btih:..."
      }
    },
    {
      "eventType": "grab_succeeded",
      "timestamp": "2026-05-09T14:30:06Z"
    },
    {
      "eventType": "detection_attempted",
      "timestamp": "2026-05-09T14:35:00Z",
      "details": { "pollerClientId": "qbittorrent-1" }
    },
    {
      "eventType": "detection_succeeded",
      "timestamp": "2026-05-09T14:35:01Z",
      "details": { "torrentHash": "abc123...", "progress": 0.25 }
    },
    {
      "eventType": "completion_detected",
      "timestamp": "2026-05-09T15:00:00Z",
      "details": { "totalSize": 4700000000, "downloadTime": 1500 }
    },
    {
      "eventType": "import_attempted",
      "timestamp": "2026-05-09T15:00:30Z",
      "details": { "sourceFilePath": "/downloads/The.Matrix.mkv" }
    },
    {
      "eventType": "import_succeeded",
      "timestamp": "2026-05-09T15:15:00Z",
      "details": { "targetFilePath": "/media/movies/The Matrix (1999).mkv" }
    }
  ]
}
```

---

#### GET /api/v1/download-dispatches/unresolved
Find dispatches that were grabbed but not detected in client (debugging aid).

**Query Parameters:**
- `minAgeMinutes`: only show grabbed before this time (default 30)
- `clientId`: optional filter
- `pageSize`, `pageToken`: pagination

**Response (200 OK):**
```json
{
  "unresolvedCount": 3,
  "dispatches": [
    {
      "id": "dispatch-789",
      "releaseName": "Some.Movie.1080p",
      "downloadClientName": "qBittorrent Main",
      "grabStatus": "succeeded",
      "grabAttemptedUtc": "2026-05-09T12:00:00Z",
      "minutesSinceGrab": 150,
      "detectionStatus": "undetected",
      "notes": "Not found in client queue. Possible: client restarted, grab response was false positive, release name mismatch"
    }
  ]
}
```

---

#### GET /api/v1/import-resolutions
Query import outcomes for external tools (fileflows, bazarr, etc.).

**Query Parameters:**
- `status`: imported, failed (default: imported)
- `importedAfter`, `importedBefore`: ISO datetime range
- `libraryId`, `mediaType`: filtering
- `pageSize`, `pageToken`: pagination

**Response (200 OK):**
```json
{
  "resolutions": [
    {
      "id": "resolution-123",
      "dispatchId": "dispatch-123",
      "entityId": "movie-456",
      "mediaType": "movie",
      "libraryId": "lib-movies",
      
      "status": "imported",           // imported, failed
      "filePath": "/media/movies/The Matrix (1999).mkv",
      "fileName": "The Matrix (1999).mkv",
      "fileSize": 4700000000,
      "importedUtc": "2026-05-09T15:15:00Z",
      
      // If failed:
      "failureCode": null,             // QUALITY_TOO_LOW, UNMATCHED_FILE, SAMPLE, CORRUPT
      "failureMessage": null,
      "failedUtc": null
    }
  ],
  "nextPageToken": "cursor_def456...",
  "hasMore": false
}
```

---

#### POST /api/v1/download-dispatches/{dispatchId}/retry
Manually retry a failed grab. Only works if status is "failed".

**Request Body:**
```json
{
  "reason": "manual_user_request"
}
```

**Response (202 Accepted):**
```json
{
  "dispatchId": "dispatch-123",
  "newJobId": "job-789",
  "nextRetryEligibleUtc": "2026-05-09T15:30:00Z",
  "message": "Retry queued"
}
```

---

#### DELETE /api/v1/download-dispatches/{dispatchId}
Archive/delete a dispatch (soft delete). Only for old, unresolved items or after manual review.

**Query Parameters:**
- `reason`: manual_cleanup, retention_policy, duplicate, etc.

**Response (204 No Content)**

---

### Data Models (Updated Contracts)

**DownloadDispatchItem** (from contracts)

```csharp
record DownloadDispatchItem(
    string Id,
    string LibraryId,
    string MediaType,
    string EntityType,
    string EntityId,
    string ReleaseName,
    string IndexerName,
    string DownloadClientId,
    string DownloadClientName,
    
    // NEW: Grab outcome
    string GrabStatus,                  // succeeded, failed, circuitOpen
    DateTimeOffset GrabAttemptedUtc,
    int? GrabResponseCode,              // HTTP status
    string? GrabMessage,                // Short description
    string? GrabFailureCode,            // GRAB_FAILED, AUTH_FAILED, TIMEOUT, etc.
    string? GrabResponseJson,           // Full client response (for debugging)
    
    // NEW: Detection (polling result)
    string DetectionStatus,             // undetected, detected, importing, imported, failed
    DateTimeOffset? DetectedUtc,
    string? TorrentHashOrItemId,        // For matching back to client
    long? DownloadedBytes,
    
    // NEW: Import outcome
    string? ImportStatus,               // undetected, detected, importing, imported, failed
    DateTimeOffset? ImportDetectedUtc,
    DateTimeOffset? ImportCompletedUtc,
    string? ImportedFilePath,
    string? ImportFailureCode,          // QUALITY_TOO_LOW, UNMATCHED, SAMPLE, CORRUPT
    string? ImportFailureMessage,
    
    // Metadata
    string? NotesJson,
    DateTimeOffset CreatedUtc
);
```

**DispatchTimelineEvent** (new, for timeline endpoint)

```csharp
record DispatchTimelineEvent(
    string EventType,                   // created, grab_attempted, grab_succeeded, grab_failed, etc.
    DateTimeOffset Timestamp,
    Dictionary<string, object>? Details // Event-specific data (client response, file path, etc.)
);
```

**ImportResolutionItem** (for import results endpoint)

```csharp
record ImportResolutionItem(
    string Id,
    string DispatchId,
    string EntityId,
    string MediaType,
    string LibraryId,
    string Status,                      // imported, failed
    string? FilePath,
    string? FileName,
    long? FileSize,
    DateTimeOffset? ImportedUtc,
    string? FailureCode,                // QUALITY_TOO_LOW, UNMATCHED, SAMPLE, CORRUPT
    string? FailureMessage,
    DateTimeOffset? FailedUtc
);
```

---

### Error Code Reference

**Grab Failure Codes:**
- `GRAB_FAILED`: Client rejected grab (generic)
- `AUTH_FAILED`: Authentication error with client
- `TIMEOUT`: Client request timed out
- `CIRCUIT_OPEN`: Circuit breaker active (too many recent failures)
- `INVALID_RELEASE`: Malformed release (missing URL, invalid category, etc.)
- `CLIENT_OFFLINE`: Client unreachable

**Import Failure Codes:**
- `QUALITY_TOO_LOW`: File quality below configured minimum
- `UNMATCHED_FILE`: File could not be matched to entity
- `SAMPLE`: Detected as sample/intro file
- `CORRUPT`: File failed validation (CRC, hash, etc.)
- `DUPLICATE`: Identical file already imported
- `INVALID_FORMAT`: File format not supported
- `DISK_FULL`: Out of disk space during import

---

## Success Criteria (Overall)

When Phase 1-4 are complete:

1. ✅ Every grab is recorded with its outcome (success, failed, circuit open, etc.)
2. ✅ We can query grabs by client, status, date range
3. ✅ Every download is matched back to our grab (or logged as unmatched)
4. ✅ Import outcomes are recorded (success, failure reason)
5. ✅ Unresolved grabs are identified and logged
6. ✅ Failed grabs trigger retry with exponential backoff
7. ✅ Old, unresolved grabs are cleaned up on schedule
8. ✅ Dashboard shows grab history and current status
9. ✅ Activity feed shows all outcomes
10. ✅ Integration tests cover all major flows

---

## Estimated Breakdown

| Phase | Tasks | Effort | Risk |
|-------|-------|--------|------|
| 1 | Migration + Repo + Service update + API endpoints + Tests | 5-6 days | Low |
| 2 | Migration + Repo + Service integration + Tests | 4-5 days | Medium (import pipeline integration) |
| 3 | Polling service + Protocol adapters + Worker + Tests | 5-6 days | Medium (client polling accuracy) |
| 4 | Retry service + Cleanup + Worker + Tests | 4-5 days | Low |
| **Total** | | **19-22 days** | |

**Buffer for integration issues: 3-5 days**

**Total: 3-4 weeks** ✅

*Note: Phase 1 now includes full API design for external tool consumption (fileflows, bazarr, etc.), adding ~1 day to the effort. This is critical for self-contained architecture.*

---

## Next Step

Ready to start Phase 1 implementation. First task:

1. Design DownloadDispatchesRepository interface
2. Create V0003 migration (schema changes)
3. Implement repository with RecordGrabAsync and query methods
4. Update DownloadClientGrabService to persist outcomes
5. Write integration tests

Proceed?
