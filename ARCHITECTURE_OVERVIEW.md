# Deluno Architecture Overview

## System Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    Client Tier (React)                      │
├─────────────────────────────────────────────────────────────┤
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────────┐  │
│  │   Movies     │  │   Series     │  │   Admin Panel    │  │
│  │   Library    │  │   Library    │  │   (Settings)     │  │
│  └──────────────┘  └──────────────┘  └──────────────────┘  │
│         ↓                  ↓                    ↓            │
│  ┌──────────────────────────────────────────────────────┐   │
│  │         LibraryViewWithBulkOps (Phase 7)            │   │
│  │      ┌────────────────────────────────────┐         │   │
│  │      │  BulkOperationsPanel (Phase 6)    │         │   │
│  │      │  - Operation Selector             │         │   │
│  │      │  - Configuration UI               │         │   │
│  │      │  - Result Display                 │         │   │
│  │      └────────────────────────────────────┘         │   │
│  └──────────────────────────────────────────────────────┘   │
├─────────────────────────────────────────────────────────────┤
│                 Shared Services & Hooks                     │
├─────────────────────────────────────────────────────────────┤
│  useErrors │ useSignalR │ useFilterPresets │ useAuth       │
└──────────┬──────────────────────────────────────────────────┘
           │  
           ├─────────→ REST API Calls
           └─────────→ SignalR Events
                       
┌──────────────────────────────────────────────────────────────┐
│                   API Tier (.NET 10)                        │
├──────────────────────────────────────────────────────────────┤
│ ┌───────────────────────────────────────────────────────┐    │
│ │          Minimal API Endpoints (Phase 7)             │    │
│ │  - POST /api/movies/bulk                            │    │
│ │  - POST /api/series/bulk                            │    │
│ │  - GET /api/movies/{id}                             │    │
│ │  - GET /api/series/{id}                             │    │
│ │  - ... (100+ endpoints total)                        │    │
│ └───────────────────────────────────────────────────────┘    │
├──────────────────────────────────────────────────────────────┤
│              Business Logic Layer (Phase 6)                 │
├──────────────────────────────────────────────────────────────┤
│ ┌──────────────────┐  ┌──────────────────┐                  │
│ │  Movie Service   │  │  Series Service  │                  │
│ │  - DeleteAsync   │  │  - DeleteAsync   │                  │
│ │  - BulkOps       │  │  - BulkOps       │                  │
│ └──────────────────┘  └──────────────────┘                  │
├──────────────────────────────────────────────────────────────┤
│               Data Access Layer (Phase 5-6)                 │
├──────────────────────────────────────────────────────────────┤
│ ┌──────────────────────────────────────────────────────┐    │
│ │  Repository Pattern (IMovieCatalogRepository, etc)  │    │
│ │  - Query operations                                 │    │
│ │  - CRUD operations                                  │    │
│ │  - Bulk operations                                  │    │
│ └──────────────────────────────────────────────────────┘    │
├──────────────────────────────────────────────────────────────┤
│                 Infrastructure Layer                        │
├──────────────────────────────────────────────────────────────┤
│ ┌──────────────┐  ┌──────────────┐  ┌────────────────┐      │
│ │   SQLite     │  │   SignalR    │  │ Background     │      │
│ │   Database   │  │   Hub        │  │ Job Queue      │      │
│ └──────────────┘  └──────────────┘  └────────────────┘      │
│                (Phases 3-4 Realtime & Jobs)                 │
└──────────────────────────────────────────────────────────────┘
```

---

## Component Hierarchy

### Frontend Component Tree

```
App
├── Router
│   ├── MoviesPage (uses LibraryViewWithBulkOps)
│   │   └── LibraryViewWithBulkOps
│   │       ├── LibraryView (main grid/list display)
│   │       └── BulkOperationsPanel
│   │           ├── OperationSelector
│   │           ├── OperationConfig
│   │           ├── WarningAlert (for destructive ops)
│   │           └── ResultDisplay
│   │               ├── ResultSummary
│   │               └── ResultDetails
│   │
│   ├── ShowsPage (uses LibraryViewWithBulkOps)
│   │   └── LibraryViewWithBulkOps
│   │       ├── LibraryView
│   │       └── BulkOperationsPanel
│   │
│   ├── DashboardPage
│   │   ├── Stats
│   │   ├── QueueOverview
│   │   └── RecentActivity
│   │
│   ├── QueuePage
│   │   ├── JobList
│   │   └── JobDetail
│   │
│   └── SettingsPage
│       ├── GeneralSettings
│       ├── QualityProfiles
│       ├── CustomFormats
│       └── etc.
│
└── Toaster (global notifications)
```

---

## Data Flow Diagrams

### Bulk Operations Flow

```
User selects items
       ↓
LibraryViewWithBulkOps.handleSelectItem()
       ↓
State updated: selectedIds []
       ↓
Selection toolbar displayed
       ↓
User clicks "Bulk Operations"
       ↓
BulkOperationsPanel rendered
       ↓
User selects operation & parameters
       ↓
User clicks "Execute"
       ↓
POST /api/{movies|series}/bulk
       ↓
Backend processes each item
       ↓
BulkOperationResponse returned
       ↓
handleBulkOperationComplete()
       ↓
Results displayed in panel
       ↓
User clicks "Done"
       ↓
Panel closes
Selection cleared
Library reloaded
```

### Real-Time Event Flow (Phase 3)

```
Backend Event Triggered
       ↓
SignalR Hub.SendAsync("eventName", data)
       ↓
SignalR Connection
       ↓
useSignalR Hook
       ↓
Event listener callback
       ↓
State update
       ↓
Component re-render
       ↓
UI updated
```

---

## Database Schema

### Movies Table (Deluno.Movies)
```sql
CREATE TABLE movies (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    year INTEGER,
    path TEXT,
    quality_profile_id TEXT,
    monitored BOOLEAN DEFAULT 1,
    added_utc DATETIME,
    updated_utc DATETIME,
    FOREIGN KEY (quality_profile_id) REFERENCES quality_profiles(id)
);
```

### Series Table (Deluno.Series)
```sql
CREATE TABLE series (
    id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    path TEXT,
    quality_profile_id TEXT,
    monitored BOOLEAN DEFAULT 1,
    added_utc DATETIME,
    updated_utc DATETIME,
    FOREIGN KEY (quality_profile_id) REFERENCES quality_profiles(id)
);
```

### Key Indexes for Performance
```sql
-- Bulk operations queries
CREATE INDEX idx_movies_monitored ON movies(monitored);
CREATE INDEX idx_series_monitored ON series(monitored);
CREATE INDEX idx_movies_quality ON movies(quality_profile_id);
CREATE INDEX idx_series_quality ON series(quality_profile_id);
```

---

## API Endpoints Reference

### Bulk Operations (Phase 6-7)

#### POST /api/movies/bulk
```json
{
  "movieIds": ["id1", "id2", "id3"],
  "operation": "monitoring|quality|search|remove",
  "monitored": true,  // for monitoring operation
  "qualityProfileId": "profile-id"  // for quality operation
}
```

**Response:**
```json
{
  "totalProcessed": 3,
  "successCount": 2,
  "failureCount": 1,
  "operation": "monitoring",
  "results": [
    {
      "itemId": "id1",
      "itemTitle": "Movie Title",
      "succeeded": true
    },
    {
      "itemId": "id2",
      "itemTitle": "Movie 2",
      "succeeded": false,
      "errorMessage": "Not found"
    }
  ]
}
```

#### POST /api/series/bulk
- Same structure as /api/movies/bulk
- Uses `seriesIds` instead of `movieIds`

---

## State Management Patterns

### Component State
```typescript
// LocalStorage for preferences
const [cardSize, setCardSize] = useState<CardSize>("md");
useEffect(() => {
  localStorage.setItem("card-size", cardSize);
}, [cardSize]);

// Selection state
const [selectedIds, setSelectedIds] = useState<string[]>([]);

// UI state
const [isShowingBulkOps, setIsShowingBulkOps] = useState(false);
```

### Custom Hooks
```typescript
// Error management
const { errors, addError, clearErrors } = useErrors();

// Real-time updates
const { onEvent } = useSignalR();

// Filter presets
const { filters, saveFilter, loadFilter } = useFilterPresets();
```

---

## Error Handling Strategy

### Backend Error Responses
```csharp
// Validation error
return BadRequest(new { message = "Invalid input", details = ... });

// Not found
return NotFound(new { message = "Item not found" });

// Server error
return StatusCode(500, new { message = "Internal server error" });

// Conflict (duplicate)
return Conflict(new { message = "Item already exists" });
```

### Frontend Error Handling
```typescript
// Try-catch in async operations
try {
  const response = await fetch(endpoint, { method: "POST" });
  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`);
  }
  return await response.json();
} catch (error) {
  toast.error("Operation failed", {
    description: error instanceof Error ? error.message : "Unknown error"
  });
}

// Error boundaries for rendering
<ErrorBoundary fallback={<ErrorView />}>
  <Component />
</ErrorBoundary>
```

---

## Performance Considerations

### Frontend Optimization
1. **Code Splitting**
   - Route-based splitting
   - Component lazy loading
   - Feature-based bundles

2. **Memoization**
   - useMemo for expensive computations
   - React.memo for components
   - useCallback for handlers

3. **Virtual Scrolling**
   - For large lists (1000+ items)
   - TanStack Table integration
   - Window-based rendering

### Backend Optimization
1. **Database Queries**
   - Indexed columns for filters
   - Parameterized queries
   - Connection pooling

2. **Caching**
   - Metadata cache (TMDb)
   - Frequent queries
   - SignalR message batching

3. **Async Processing**
   - Background job queue
   - Bounded channels for backpressure
   - Graceful degradation

---

## Testing Strategy

### Unit Tests
- Individual component behavior
- Hook logic
- Utility functions
- Service methods

### Integration Tests
- Component + API interaction
- Database operations
- Error handling paths

### E2E Tests
- User workflows
- Bulk operations (Phase 6-7)
- Responsive design
- Accessibility compliance

### Performance Tests
- Page load time
- List rendering performance
- API response time
- Memory usage

---

## Security Considerations

### Input Validation
- Frontend validation for UX
- Backend validation for security
- SQL injection prevention (parameterized queries)
- XSS prevention (React escaping)

### Authentication/Authorization
- User token validation
- Role-based access control (RBAC)
- Endpoint-level authorization
- Session management

### Data Protection
- HTTPS for transport
- Data encryption at rest
- Sensitive data logging restrictions
- GDPR compliance (if applicable)

---

## Deployment Architecture

### Development
- Local development with hot reload
- SQLite database
- Console output logging
- Feature flags for testing

### Staging
- Docker container setup
- Test database
- Monitoring integration
- Performance testing

### Production
- Multi-instance deployment
- Database backups
- CloudFlare/CDN integration
- Monitoring and alerting

---

## Future Extensibility

### Plugin System
```csharp
public interface IMediaPlugin
{
    Task<List<MediaItem>> SearchAsync(string query);
    Task<bool> DownloadAsync(string itemId);
}
```

### Custom Endpoints
```typescript
// Allow custom API extensions
const customEndpoints = useFetchCustomEndpoints();
```

### Theme Customization
```typescript
// CSS variables for theming
const theme = {
  primary: "var(--color-primary)",
  secondary: "var(--color-secondary)"
};
```

---

**Last Updated:** May 10, 2026  
**Applies to:** Phases 1-7 Complete, Ready for Phase 8+  
**Architecture Grade:** A- (Production Ready with enhancements)
