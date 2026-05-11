# Design Decisions for Issue #23: Real Integration & Download Plumbing

**Document Purpose:** Lock in architectural decisions before Phase 1 implementation. These decisions are binding unless explicitly revisited.

**Decision Template:**
```
Decision: [Short name]
Status: [Proposed / Locked / Needs Review]
Rationale: [Why this choice]
Implications: [What this affects]
```

---

## D1: Recording Transactionality

**Decision:** Grab AND recording must both succeed, or both fail (atomic).

**Status:** Locked

**Rationale:** 
- If grab succeeds but recording fails, we lose telemetry and the user is confused ("Why didn't this show up?")
- If recording succeeds but grab fails, we have a phantom dispatch that never existed in the client
- External tools (fileflows, bazarr) rely on dispatch records being authoritative; corruption breaks downstream trust
- Atomicity is achievable via job-queue pattern: enqueue both operations as single transaction, execute both or neither

**Implementation:**
```csharp
// In DownloadClientGrabService.GrabAsync:
try {
    var grabResult = await client.GrabAsync(request);
    
    // Atomic: record grab outcome in job queue
    var dispatchJob = new RecordDownloadGrabJob {
        DispatchId = dispatchId,
        GrabResult = grabResult,
        ResponseJson = grabResult.ResponseJson
    };
    
    await jobStore.EnqueueAsync(dispatchJob, idempotencyKey: dispatchId);
    // If this throws, entire grab is retried
}
catch (Exception ex) {
    // Grab failed; no record created
    // External tools won't see this failed attempt
    // Next search cycle will try again
}
```

**Implications:**
- Grab and record are 1-2 ms apart, not instantaneous
- If recording service crashes before dequeue, grab is retried on next search
- No "phantom" grabs that happened but weren't recorded
- Retry logic is external to GrabService (handled by caller)

---

## D2: Polling Interval & Worker Configuration

**Decision:** Polling runs every 5 minutes by default, configurable via appsettings.

**Status:** Locked

**Rationale:**
- 5 minutes balances responsiveness (imports detected quickly) with API burden (12 polls/hour per client)
- Too fast (1 min): excessive client API hits, database churn
- Too slow (30 min): users see delayed completion, triggers manual refresh requests
- Configurable for power users: they may want slower for bandwidth limits or faster for high-turnover

**Implementation:**
```json
// appsettings.json
{
  "Deluno": {
    "Download": {
      "PollingIntervalSeconds": 300,        // 5 minutes
      "PollingBatchSize": 50,               // Max dispatches to poll per run
      "PollingMaxConcurrency": 3            // Max concurrent client connections
    }
  }
}
```

**Worker Configuration:**
- `DownloadPollingWorker` registered in background job system
- Runs on timer loop (not recurring job)
- Each iteration: poll all enabled clients, match items to dispatches, record detection
- Skips clients with circuit breaker open (paused after 3 failures)
- Logs per-client timing and item counts

**Implications:**
- Default 5 min means 288 polls/day per client (small overhead)
- With configurable setting, users can trade off latency vs. bandwidth
- Worker runs in-process (not separate service), so single deployment needed

---

## D3: Cleanup Retention Policy

**Decision:** Keep dispatches for 7 days by default. Three retention tiers based on status.

**Status:** Locked

**Rationale:**
- **Imported successfully:** Keep indefinitely (audit trail)
- **Failed imports:** Keep for 7 days (user may troubleshoot or retry)
- **Unresolved (never detected):** Keep for 7 days (debugging), then archive as "abandoned"
- 7 days balances: enough time for user to investigate, but doesn't bloat database

**Implementation:**
```csharp
public class DownloadCleanupPolicy {
    // Keep successful imports forever for audit trail
    public int SuccessfulImportRetentionDays => int.MaxValue;
    
    // Keep failed imports for troubleshooting
    public int FailedImportRetentionDays => 7;
    
    // Keep unresolved grabs for debugging
    public int UnresolvedGrabRetentionDays => 7;
    
    // Maximum dispatches to archive per cleanup run
    public int MaxDispatchesPerCleanupRun => 1000;
}
```

**Cleanup Worker:**
- Runs daily at 2am (off-peak)
- Finds dispatches older than retention period
- Soft-delete (mark archived, don't remove rows)
- Log archived count and reasons to activity feed
- External tools can still query archived items (API doesn't filter them)

**Implications:**
- Archive is permanent (soft delete can't be undone without DB edit)
- Archived items show in API with `status: "archived"`
- Activity feed logs all cleanup actions
- Very large libraries (10K+ dispatches/week) will grow DB by ~10MB/month

---

## D4: API Exposure & External Tool Integration

**Decision:** Full API exposure. All dispatch and import data is queryable via REST endpoints. No restrictions on what external tools can access.

**Status:** Locked

**Rationale:**
- fileflows, bazarr, and other tools MUST have authoritative data
- Self-contained architecture means Deluno doesn't depend on external APIs (so it doesn't force dependency on us)
- Transparency builds trust: users can audit Deluno's decisions
- Structured error codes + timeline events enable intelligent retry logic in external tools

**API Tier Design:**
```
Tier 1 (Dispatch Summary):
  GET /api/v1/download-dispatches
  GET /api/v1/download-dispatches/{id}
  Payload: grab status, detection status, import status
  Use: Dashboard, quick status checks

Tier 2 (Timeline & Audit):
  GET /api/v1/download-dispatches/{id}
  Payload: full timeline of all events
  Use: Debugging, external tool coordination

Tier 3 (Import Results):
  GET /api/v1/import-resolutions
  Payload: file path, success/failure, error codes
  Use: fileflows, bazarr, other processors
  
Tier 4 (Unresolved):
  GET /api/v1/download-dispatches/unresolved
  Payload: grabbed but not detected in client
  Use: Debugging, retry logic
```

**Authentication:**
- API keys with scopes: `read:downloads`, `read:imports`, `write:retry`
- No token expiry by default (optional)
- Stored in settings database

**Implications:**
- Every download dispatch is queryable (including failed ones)
- Import results expose file paths (needed for downstream tools)
- External tools can poll API instead of hooking into Deluno
- Webhook events (v1.1) will supplement polling for real-time
- All API responses are versioned (/api/v1) for backward compatibility

---

## D5: Failure Code Structure

**Decision:** Use structured error codes (not text blobs) for grab and import failures.

**Status:** Locked

**Rationale:**
- External tools need to decide on retry strategy: Some errors (TIMEOUT) are retryable; others (INVALID_RELEASE) are not
- Text messages are for humans ("API timeout, retrying in 30s..."); codes are for machines
- Standardized codes enable batch operations: "retry all TIMEOUT failures"
- Error code ref is in API_CONTRACT_EXTERNAL_TOOLS.md

**Grab Failure Codes:**
- `GRAB_FAILED` (generic, client rejected)
- `AUTH_FAILED` (credentials wrong)
- `TIMEOUT` (client slow to respond)
- `CIRCUIT_OPEN` (too many failures, paused)
- `INVALID_RELEASE` (bad payload)
- `CLIENT_OFFLINE` (unreachable)

**Import Failure Codes:**
- `QUALITY_TOO_LOW` (below min quality)
- `UNMATCHED_FILE` (couldn't identify entity)
- `SAMPLE` (detected as intro/sample)
- `CORRUPT` (file validation failed)
- `DUPLICATE` (already imported)
- `INVALID_FORMAT` (unsupported format)
- `DISK_FULL` (no space left)

**Implications:**
- Each code is ~20 chars, stored in DB
- Response message is SHORT human text ("File quality 720p is below minimum 1080p")
- External tools can parse code for logic; message is for UI display

---

## D6: Timeline Events vs. State Snapshots

**Decision:** API exposes both state snapshot AND timeline events (not either/or).

**Status:** Locked

**Rationale:**
- State snapshot (grabStatus, importStatus) answers "where is this now?"
- Timeline (array of events with timestamps) answers "what happened and when?"
- fileflows/bazarr need both: state to decide action, timeline to debug

**State Fields** (on dispatch object):
```
grabStatus: "succeeded" | "failed" | ...
detectionStatus: "undetected" | "detected" | ...
importStatus: "undetected" | "imported" | "failed"
importedUtc, importCompletedUtc, importFailureCode
```

**Timeline Events** (separate array):
```
[
  { eventType: "created", timestamp, details },
  { eventType: "grab_attempted", timestamp, details },
  { eventType: "import_succeeded", timestamp, details }
]
```

**Implications:**
- DB stores both: state columns + event log
- Dispatch object size increases (30 → 50 fields)
- Timeline queries need index on (dispatchId, timestamp)
- External tools can validate state by replaying timeline

---

## D7: Idempotency & Duplicate Grabs

**Decision:** Same release grabbed twice (race condition) is detected and deduplicated.

**Status:** Locked

**Rationale:**
- During search cycle, same movie can be evaluated twice if user manually triggers search while auto-search runs
- Result: two grabs sent for same release within seconds
- Both succeed on client, but we only want one dispatch record
- Deduplication key: (entityId, releaseName, clientId) within 30-second window

**Implementation:**
```csharp
var deduplicationKey = $"{entityId}:{releaseName}:{downloadClientId}";
var dispatchId = await dispatchStore.GetOrCreateAsync(
    deduplicationKey,
    () => CreateNewDispatch()
);
```

**Implications:**
- Grab within 30s with same release on same client returns existing dispatch
- Same release on DIFFERENT client is allowed (user may want fallback)
- Deduplication is per-library (lib A and lib B can grab same title independently)

---

## D8: Circuit Breaker Persistence

**Decision:** Circuit breaker state is persisted in database, not in-memory.

**Status:** Locked

**Rationale:**
- If Deluno restarts, in-memory circuit state is lost
- Client might be still down, but Deluno doesn't know
- Consequences: spam 3+ failed grabs at restart, overwhelming client or alerting user falsely
- Persistence means circuit state survives restart

**Implementation:**
- Stored in download_dispatches table as `circuit_open_until_utc`
- Per-client: if client has 3 failures in 5 minutes, pause for 30 minutes
- Next grab attempt checks: if circuit_open_until > now, return CIRCUIT_OPEN
- Background job resets circuit weekly (safety reset even if all grabs fail)

**Implications:**
- Client offline is detected automatically, won't spam grabs
- Circuit resets after timeout (doesn't require manual intervention)
- Manual retry endpoint respects circuit (can't force grab if circuit open)

---

## D9: Search Retry Window Integration

**Decision:** Failed grabs update SearchRetryWindow with exponential backoff.

**Status:** Locked

**Rationale:**
- If grab fails, user expects retry, not silence
- Exponential backoff prevents thrashing: 30s → 2 min → 10 min → 1 hr
- SearchRetryWindow already tracks retry state for searches; we extend it for grabs
- User can manually retry via API endpoint (resets backoff)

**Implementation:**
```csharp
if (grabResult.Failed) {
    await retryWindowStore.UpdateNextEligibleAsync(
        entityId: dispatch.EntityId,
        actionKind: "grab_retry",
        exponentialBackoff: true  // Doubles delay each time
    );
}
```

**Retry Delays:**
- 1st attempt: immediate
- 1st failure: retry after 30 seconds
- 2nd failure: retry after 2 minutes
- 3rd failure: retry after 10 minutes
- 4th failure: retry after 1 hour
- 5th+ failure: retry after 4 hours
- Manual retry: resets to immediate

**Implications:**
- Failed grabs are automatically retried, user doesn't need to act
- After many failures, retry window backs off to avoid spam
- Manual retry endpoint overrides backoff for user-initiated retries

---

## D10: Job-Based Recording vs. Synchronous

**Decision:** Use job-queue (asynchronous) for recording, not synchronous database call.

**Status:** Locked

**Rationale:**
- If DB write fails synchronously, we lose the grab outcome
- Job-queue ensures recording is retried until success
- Grab service doesn't block on I/O (fast feedback to caller)
- Polling worker processes jobs at its own pace

**Flow:**
```
1. GrabAsync completes, returns result
2. Enqueue RecordGrabJob (idempotent)
3. Return immediately to caller
4. Background worker dequeues, writes to DB
5. If DB write fails, job is retried
```

**Implications:**
- Grab and record are decoupled (1-5 sec delay normal)
- Job queue is single source of truth for dispatch state
- If job queue service crashes, restart dequeues and processes
- External tools polling API see slight delay in data (queue processing)

---

## D11: Polling Matching Strategy

**Decision:** Match client queue items to dispatches by name similarity, then by torrent hash/item ID.

**Status:** Locked (Phase 3, not Phase 1)

**Rationale:**
- Release name is user-friendly but can be aliased (e.g., "The Matrix" vs. "The Matrix (1999)")
- Torrent hash is reliable but not always available (Usenet clients don't have hashes)
- Two-stage matching: fuzzy name match first, then verify by hash

**Matching Rules:**
1. Hash match (if available): primary key
2. Release name similarity (90%+): secondary
3. Unmatched items logged for debugging

**Implications:**
- Matching has configurable threshold (default 90%)
- Some items may not match (user gets unresolved notification)
- External tools can read unresolved dispatch list via API

---

## D12: Webhook Support (Deferred)

**Decision:** Webhooks are v1.1 feature, not in Phase 1.

**Status:** Locked (Deferred)

**Rationale:**
- Polling every 5 min is sufficient for MVP
- Webhooks add complexity: delivery, retries, authentication
- Most external tools prefer polling (simpler integration)
- Real-time updates (SignalR) can bridge gap for dashboard

**v1.1 Plan:**
- Webhook endpoint registration (Settings UI)
- Events: dispatch.grabbed, dispatch.imported, dispatch.failed
- Delivery with exponential backoff
- Signature verification (HMAC-SHA256)

**Implications:**
- Phase 1 is simpler (no webhook infra)
- External tools use polling (GET /api/v1/import-resolutions?importedAfter=...)
- SignalR updates dashboard in real-time (separate from API)

---

## Summary of Locked Decisions

| # | Decision | Choice | Risk |
|----|----------|--------|------|
| D1 | Transactionality | Atomic (grab + record, both or neither) | Low |
| D2 | Polling Interval | 5 minutes (configurable) | Low |
| D3 | Cleanup Retention | 7 days (success indefinite) | Low |
| D4 | API Exposure | Full (no restrictions) | Low |
| D5 | Error Codes | Structured (not text blobs) | Low |
| D6 | State vs. Timeline | Both (state + timeline events) | Low |
| D7 | Deduplication | 30-second window (same release) | Very Low |
| D8 | Circuit Breaker | Persisted (survives restart) | Very Low |
| D9 | Retry Integration | Exponential backoff via SearchRetryWindow | Low |
| D10 | Recording Pattern | Job-queue (async, with retries) | Low |
| D11 | Polling Matching | Hash-first, then fuzzy name | Medium |
| D12 | Webhooks | Deferred to v1.1 | Very Low |

---

## Next Steps

1. ✅ API contract designed (API_CONTRACT_EXTERNAL_TOOLS.md)
2. ✅ Implementation plan updated (IMPLEMENTATION_PLAN_23.md)
3. ✅ Design decisions locked (this document)
4. **TODO:** Create migration schema (V0003DownloadOutcomeTracking.cs)
5. **TODO:** Implement DownloadDispatchStore interface and SqliteDownloadDispatchStore
6. **TODO:** Build API endpoints (DownloadDispatchesController.cs)
7. **TODO:** Write tests (DownloadClientGrabPersistenceTests.cs, DownloadDispatchesApiTests.cs)

---

## Revisiting Decisions

These decisions can be revisited if:
- Real usage contradicts assumptions (e.g., 5-min polling too frequent)
- Implementation reveals unforeseen complexity
- External tools provide feedback on API shape

To revisit: Open GitHub issue with `[Design Review]` tag, link this document, explain new constraint.
