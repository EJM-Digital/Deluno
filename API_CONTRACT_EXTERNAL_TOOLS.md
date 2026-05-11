# Deluno API Contract for External Tools

**Purpose:** Enable external tools (fileflows, bazarr, etc.) to reliably consume Deluno download and import tracking data.

**Stability:** v1 - This is the contract that external tools can build on. Breaking changes require major version bump.

**Last Updated:** 2026-05-09

---

## Authentication

All API endpoints require an API key passed as a header:

```
Authorization: Bearer YOUR_API_KEY
```

**Getting an API key:** 
- Go to Settings → API Keys
- Create a key with scope: `read:downloads`, `read:imports`
- Keys do not expire by default (optional expiry can be set)

---

## Base URL

```
http://deluno-host:port/api/v1
```

**Example:**
```
http://localhost:5000/api/v1
```

---

## Core Use Case: "What files did Deluno import?"

### For fileflows (subtitle/metadata processing)

**Query:** "Show me all files imported in the last hour"

```bash
GET /api/v1/import-resolutions?status=imported&importedAfter=2026-05-09T14:00:00Z&pageSize=100
Authorization: Bearer YOUR_API_KEY
```

**Response:**
```json
{
  "resolutions": [
    {
      "id": "resolution-123",
      "dispatchId": "dispatch-123",
      "entityId": "movie-456",
      "mediaType": "movie",
      "libraryId": "lib-movies",
      "status": "imported",
      "filePath": "/media/movies/The Matrix (1999)/The Matrix (1999).mkv",
      "fileName": "The Matrix (1999).mkv",
      "fileSize": 4700000000,
      "importedUtc": "2026-05-09T14:15:00Z",
      "failureCode": null,
      "failureMessage": null
    }
  ],
  "nextPageToken": null,
  "hasMore": false
}
```

**Then fileflows can:**
1. Fetch subtitles for `/media/movies/The Matrix (1999)/The Matrix (1999).mkv`
2. Check if the file is a movie or TV episode
3. Process the file without re-scanning the library

---

### For bazarr (subtitle management)

**Query:** "Show me all imports for this movie/episode"

```bash
GET /api/v1/import-resolutions?entityId=movie-456&status=imported
Authorization: Bearer YOUR_API_KEY
```

**Response:** Same as above, filtered to that entity.

**Then bazarr can:**
1. Automatically trigger subtitle search for the imported file
2. Match the filePath to know exactly what to search for
3. Skip rescanning large libraries

---

## Detailed Endpoint Reference

### GET /api/v1/download-dispatches

**Purpose:** Query all download grabs with their outcomes. For debugging and monitoring.

**Query Parameters:**

| Parameter | Type | Required | Example | Notes |
|-----------|------|----------|---------|-------|
| status | string | No | `grabbed` | Valid: grabbed, downloading, completed, failed, archived |
| clientId | string | No | `qbittorrent-1` | Download client ID |
| entityType | string | No | `movie` | movie or episode |
| entityId | string | No | `movie-456` | Specific entity |
| libraryId | string | No | `lib-movies` | Library filter |
| minGrabTime | ISO 8601 | No | `2026-05-09T12:00:00Z` | Only grabs after this time |
| maxGrabTime | ISO 8601 | No | `2026-05-09T15:00:00Z` | Only grabs before this time |
| pageSize | int | No | `50` | 10-100, default 50 |
| pageToken | string | No | `cursor_abc...` | For pagination |

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
      
      "grabStatus": "succeeded",
      "grabAttemptedUtc": "2026-05-09T14:30:05Z",
      "grabResponseCode": 200,
      "grabMessage": "Added to queue",
      "grabFailureCode": null,
      
      "detectionStatus": "detected",
      "detectedUtc": "2026-05-09T14:35:00Z",
      "downloadedBytes": 4700000000,
      
      "importStatus": "imported",
      "importDetectedUtc": "2026-05-09T15:00:00Z",
      "importCompletedUtc": "2026-05-09T15:15:00Z",
      "importedFilePath": "/media/movies/The Matrix (1999).mkv",
      "importFailureCode": null,
      "importFailureMessage": null
    }
  ],
  "nextPageToken": null,
  "hasMore": false
}
```

**Error Responses:**
- `400 Bad Request` - Invalid query parameters
- `401 Unauthorized` - Missing/invalid API key
- `403 Forbidden` - API key lacks required scope

---

### GET /api/v1/download-dispatches/{dispatchId}

**Purpose:** Get full audit trail for a single dispatch.

**Response (200 OK):**
```json
{
  "id": "dispatch-123",
  // ... same dispatch fields as list ...
  "timeline": [
    {
      "eventType": "created",
      "timestamp": "2026-05-09T14:30:00Z",
      "details": {
        "reason": "manual_search",
        "searchJobId": "job-789"
      }
    },
    {
      "eventType": "grab_attempted",
      "timestamp": "2026-05-09T14:30:05Z",
      "details": {
        "clientResponse": {
          "code": 200,
          "message": "Added to queue"
        },
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
      "details": {
        "pollerClientId": "qbittorrent-1"
      }
    },
    {
      "eventType": "detection_succeeded",
      "timestamp": "2026-05-09T14:35:01Z",
      "details": {
        "torrentHash": "abc123def456",
        "progress": 0.25
      }
    },
    {
      "eventType": "completion_detected",
      "timestamp": "2026-05-09T15:00:00Z",
      "details": {
        "totalSize": 4700000000,
        "downloadTime": 1500
      }
    },
    {
      "eventType": "import_attempted",
      "timestamp": "2026-05-09T15:00:30Z",
      "details": {
        "sourceFilePath": "/downloads/The.Matrix.1999.mkv"
      }
    },
    {
      "eventType": "import_succeeded",
      "timestamp": "2026-05-09T15:15:00Z",
      "details": {
        "targetFilePath": "/media/movies/The Matrix (1999).mkv",
        "fileSize": 4700000000
      }
    }
  ]
}
```

**Event Types:**
- `created` - Dispatch created
- `grab_attempted` - Grab request sent to client
- `grab_succeeded` - Grab accepted by client
- `grab_failed` - Grab rejected by client
- `detection_attempted` - Polling started
- `detection_succeeded` - Found in client queue
- `detection_failed` - Polling couldn't match
- `completion_detected` - Download finished
- `import_attempted` - Import started
- `import_succeeded` - Import completed
- `import_failed` - Import rejected
- `archived` - Dispatch archived (cleanup)

---

### GET /api/v1/import-resolutions

**Purpose:** Query import results for integration tools.

**Query Parameters:**

| Parameter | Type | Required | Example | Notes |
|-----------|------|----------|---------|-------|
| status | string | No | `imported` | imported, failed |
| libraryId | string | No | `lib-movies` | Library filter |
| mediaType | string | No | `movie` | movie, episode |
| entityId | string | No | `movie-456` | Specific entity |
| importedAfter | ISO 8601 | No | `2026-05-09T12:00:00Z` | Only imports after this time |
| importedBefore | ISO 8601 | No | `2026-05-09T15:00:00Z` | Only imports before this time |
| pageSize | int | No | `50` | 10-100, default 50 |
| pageToken | string | No | `cursor_abc...` | For pagination |

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
      "status": "imported",
      "filePath": "/media/movies/The Matrix (1999)/The Matrix (1999).mkv",
      "fileName": "The Matrix (1999).mkv",
      "fileSize": 4700000000,
      "importedUtc": "2026-05-09T15:15:00Z",
      "failureCode": null,
      "failureMessage": null,
      "failedUtc": null
    }
  ],
  "nextPageToken": null,
  "hasMore": false
}
```

**Failure Response Example:**
```json
{
  "resolutions": [
    {
      "id": "resolution-124",
      "dispatchId": "dispatch-124",
      "entityId": "movie-457",
      "mediaType": "movie",
      "libraryId": "lib-movies",
      "status": "failed",
      "filePath": "/downloads/bad_file.mkv",
      "fileName": "bad_file.mkv",
      "fileSize": 2000000000,
      "importedUtc": null,
      "failureCode": "QUALITY_TOO_LOW",
      "failureMessage": "File quality 720p is below minimum 1080p",
      "failedUtc": "2026-05-09T15:20:00Z"
    }
  ],
  "nextPageToken": null,
  "hasMore": false
}
```

---

### GET /api/v1/download-dispatches/unresolved

**Purpose:** Find grabs that failed to detect in client (debugging).

**Query Parameters:**

| Parameter | Type | Required | Example | Notes |
|-----------|------|----------|---------|-------|
| minAgeMinutes | int | No | `30` | Only show if grabbed >N minutes ago |
| clientId | string | No | `qbittorrent-1` | Filter by client |
| pageSize | int | No | `50` | 10-100, default 50 |
| pageToken | string | No | `cursor_abc...` | For pagination |

**Response (200 OK):**
```json
{
  "unresolvedCount": 3,
  "dispatches": [
    {
      "id": "dispatch-999",
      "releaseName": "Some.Movie.2025.1080p.BluRay",
      "downloadClientName": "qBittorrent Main",
      "grabStatus": "succeeded",
      "grabAttemptedUtc": "2026-05-09T12:00:00Z",
      "minutesSinceGrab": 150,
      "detectionStatus": "undetected",
      "notes": "Grab succeeded but never appeared in client. Possible: client restarted, release name doesn't match client item, torrent was immediately removed."
    }
  ],
  "nextPageToken": null,
  "hasMore": false
}
```

---

### POST /api/v1/download-dispatches/{dispatchId}/retry

**Purpose:** Manually retry a failed grab.

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
  "newJobId": "job-890",
  "nextRetryEligibleUtc": "2026-05-09T15:30:00Z",
  "message": "Retry queued. Next eligibility window: 2026-05-09T15:30:00Z"
}
```

**Error Response (400 Bad Request):**
```json
{
  "code": "CANNOT_RETRY",
  "message": "Cannot retry dispatch with status 'imported'. Only 'failed' grabs can be retried."
}
```

---

### DELETE /api/v1/download-dispatches/{dispatchId}

**Purpose:** Archive/delete a dispatch (soft delete).

**Query Parameters:**

| Parameter | Type | Required | Example | Notes |
|-----------|------|----------|---------|-------|
| reason | string | Yes | `manual_cleanup` | manual_cleanup, retention_policy, duplicate |

**Response (204 No Content)**

---

## Error Code Reference

### Grab Failure Codes

Used in `grabFailureCode` field:

| Code | Meaning | External Tool Should... |
|------|---------|------------------------|
| `GRAB_FAILED` | Client rejected grab (generic) | Check client logs, verify client credentials |
| `AUTH_FAILED` | Authentication error with client | Verify API key/password for client |
| `TIMEOUT` | Client request timed out | Retry later, check client responsiveness |
| `CIRCUIT_OPEN` | Circuit breaker active (too many failures) | Wait, retry in 30+ minutes |
| `INVALID_RELEASE` | Malformed release (missing URL, invalid category) | Report to user, check release format |
| `CLIENT_OFFLINE` | Client unreachable | Check client connectivity |

### Import Failure Codes

Used in `importFailureCode` field:

| Code | Meaning | External Tool Should... |
|------|---------|------------------------|
| `QUALITY_TOO_LOW` | File quality below configured minimum | User can manually upgrade or ignore |
| `UNMATCHED_FILE` | File could not be matched to entity | Report unmatched file to user |
| `SAMPLE` | Detected as sample/intro file | Try different source, user can override |
| `CORRUPT` | File failed validation (CRC, hash, etc.) | User should re-download or delete |
| `DUPLICATE` | Identical file already imported | Inform user, suggest deletion |
| `INVALID_FORMAT` | File format not supported | Report unsupported format to user |
| `DISK_FULL` | Out of disk space during import | User must free space |

---

## Pagination

All list endpoints support cursor-based pagination:

```bash
# First page
GET /api/v1/import-resolutions?pageSize=50

# Response includes:
{
  "resolutions": [...],
  "nextPageToken": "cursor_xyz789...",
  "hasMore": true
}

# Next page
GET /api/v1/import-resolutions?pageSize=50&pageToken=cursor_xyz789...
```

**Why cursor-based?**
- Offset-based pagination breaks if data is deleted between requests
- Cursor is stable even if new items are added
- Supports large datasets efficiently

---

## Rate Limiting

**Limits:**
- 100 requests per minute per API key
- 10,000 requests per day per API key

**Headers in Response:**
```
X-RateLimit-Limit: 100
X-RateLimit-Remaining: 87
X-RateLimit-Reset: 1715339400
```

**Exceeding limit:**
```
HTTP 429 Too Many Requests
Retry-After: 60
```

---

## Webhook Events (Future)

Not implemented in v1, but planned for v1.1:

```
POST /api/v1/webhooks/register
{
  "url": "https://your-tool.com/deluno/webhooks",
  "events": ["dispatch.created", "dispatch.grab_succeeded", "import.completed"]
}
```

Events would be delivered to your URL in real-time instead of polling.

---

## Examples by Tool Type

### fileflows Integration

```python
import requests
from datetime import datetime, timedelta

API_KEY = "your_api_key_here"
BASE_URL = "http://localhost:5000/api/v1"

def get_new_imports(minutes=60):
    """Get all imports from the last N minutes"""
    since = (datetime.utcnow() - timedelta(minutes=minutes)).isoformat() + "Z"
    response = requests.get(
        f"{BASE_URL}/import-resolutions",
        params={"status": "imported", "importedAfter": since},
        headers={"Authorization": f"Bearer {API_KEY}"}
    )
    return response.json()["resolutions"]

def process_file(resolution):
    """Process a newly imported file"""
    file_path = resolution["filePath"]
    entity_id = resolution["entityId"]
    media_type = resolution["mediaType"]
    
    print(f"Processing {media_type}: {file_path}")
    
    # TODO: Download subtitles, fetch metadata, etc.
    
# Run every 5 minutes
imports = get_new_imports(minutes=5)
for imp in imports:
    process_file(imp)
```

### bazarr Integration

```python
def trigger_subtitle_search(resolution):
    """Trigger subtitle search in bazarr"""
    import subprocess
    
    # Use Deluno's file path directly
    subprocess.run([
        "bazarr-cli",
        "subtitle",
        "search",
        "--file", resolution["filePath"],
        "--media-type", resolution["mediaType"]
    ])

# Listen for imports
imports = get_new_imports(minutes=5)
for imp in imports:
    trigger_subtitle_search(imp)
```

---

## Support & Questions

- **Issues:** Report API bugs via [GitHub Issues](https://github.com/jampat000/Deluno/issues)
- **Questions:** Post to [Discussions](https://github.com/jampat000/Deluno/discussions)
- **Contributing:** External tool examples and wrappers welcome!
