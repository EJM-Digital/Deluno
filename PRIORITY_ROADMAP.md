# Deluno Priority Roadmap

Generated: 2026-05-09

## Executive Summary

Deluno is a unified media automation app consolidating Radarr, Sonarr, Prowlarr, Recyclarr, Cleanuparr, Huntarr, and Fetcher functionality. The architecture is solid (React 19 + ASP.NET Core 10 + SQLite). Core product surfaces exist but several critical features are incomplete or missing. Current quality grade: **B-**

## TIER 1: Critical Path (Ship-Blocking)

These features block the product from being self-contained and functional. Without them, users must return to external tools.

### 1. Real Integration & Download Plumbing
**Status:** Partial - simulated behavior in place  
**Impact:** HIGH - Cannot replace Radarr/Sonarr without real connections  
**Work:**
- [x] Indexer protocol adapters (torrent/usenet abstraction exists)
- [x] Download client telemetry (qBittorrent/Transmission snapshot)
- [ ] **Real download grab & import outcome tracking** - currently pull-driven, not persisted
- [ ] Historical per-client grab/item records with import resolution status
- [ ] Failed grab cleanup and retry logic with exponential backoff
- [ ] Health circuit-breaker state persistence and recovery
- [ ] Webhook support for download client push notifications (skip polling)

**Effort:** 3-4 weeks | **Blocker:** Yes, until this works, app is a UI-only shell

### 2. Episode-Level TV Workflows  
**Status:** Partial - show/season/episode state exists, operations incomplete  
**Impact:** HIGH - TV users cannot manage shows properly  
**Work:**
- [x] Episode state tracking (monitored, missing, grabbed, downloaded)
- [ ] **Episode-level wanted/upgrade decisions** - multi-episode logic incomplete
- [ ] Season pack vs. episode-by-episode routing
- [ ] Monitored episode filtering (only search monitored episodes, not all)
- [ ] Alternative episode matching (special seasons, dubs, multiple versions)
- [ ] Episode import recovery and re-import with existing file handling
- [ ] Upgrade-until logic per series (cutoff quality + custom format score)

**Effort:** 3-4 weeks | **Blocker:** Yes, TV is unusable without episode-level operations

### 3. Movie Replacement Protection & Quality Scoring
**Status:** Partial - upgrade state exists, decision logic incomplete  
**Impact:** HIGH - Users risk accidental downgrades  
**Work:**
- [x] Movie has_file and current_quality tracking
- [ ] **Safe upgrade-only behavior** - prevent downgrades (quality AND custom format score)
- [ ] Custom format scoring integration (currently exists as engine, needs UI + enforcement)
- [ ] Replacement protection decision explanation (why an upgrade was/wasn't grabbed)
- [ ] Quality cutoff handling (grab until this quality is reached)
- [ ] Multi-profile support and profile-per-library assignment
- [ ] Dry-run UI for testing quality/custom-format rules before applying

**Effort:** 2-3 weeks | **Blocker:** Yes, until this works, users cannot trust Deluno with imports

### 4. Guide-Backed Presets (Recyclarr Replacement)
**Status:** Partial - custom format matcher exists, guide import missing  
**Impact:** MEDIUM - Users cannot apply quality/naming standards  
**Work:**
- [x] Custom format condition matching (multi-condition evaluation works)
- [x] Dry-run panel for custom format testing
- [ ] **Guide import from TRaSH (or static presets)** - no external guide fetching
- [ ] Quality profile template system with safe preset selection
- [ ] Naming convention presets with before/after preview
- [ ] Guide drift detection when presets change
- [ ] Custom format score override UI with explanations
- [ ] Per-library preset targeting

**Effort:** 2-3 weeks | **Blocker:** Soft blocker - users want this early

### 5. Import Recovery & Cleanup (Cleanuparr Replacement)
**Status:** Partial - import history exists, recovery operations incomplete  
**Impact:** MEDIUM-HIGH - Users cannot fix failed imports  
**Work:**
- [x] Failed-import classification (quality, unmatched, sample, corrupt, etc.)
- [x] Queue/import preview surface
- [ ] **Automated cleanup policies** - manual actions exist, automation doesn't
- [ ] Detailed import issue classification with clear explanations
- [ ] Delete vs. remove handling (delete from disk, or just from queue)
- [ ] Recovery workflow (re-trigger import, re-search, manual assignment)
- [ ] Per-library cleanup retention policies
- [ ] Webhook notifications for critical import failures

**Effort:** 2 weeks | **Blocker:** Yes, until this works, failed imports create manual overhead

## TIER 2: MVP Completeness (v1.0 Shipping Features)

Features that need to exist for a credible MVP but aren't strictly blocking if alternatives exist temporarily.

### 6. Real Indexer Setup & Health
**Status:** Partial - indexer definitions exist, health monitoring incomplete  
**Impact:** HIGH - Users cannot debug source problems  
**Work:**
- [x] Indexer CRUD and configuration UI
- [x] Search testing (search an indexer and preview results)
- [ ] **Persistent health tracking** - indexer caps, disabled states, last-error recording
- [ ] Rate limit and timeout visibility with recovery time
- [ ] Per-indexer per-library routing rules
- [ ] Result filtering and source visibility control (show/hide by indexer)
- [ ] Indexer-to-download-client mapping UI
- [ ] Search history and result caching

**Effort:** 2-3 weeks | **Blocker:** Soft blocker - needs proper health tracking

### 7. Search Automation Completeness (Huntarr Replacement)
**Status:** Partial - search cycle jobs exist, scheduling incomplete  
**Impact:** MEDIUM - Recurring searches are limited  
**Work:**
- [x] Search cycle job tracking and execution
- [x] Cooldown and retry memory
- [ ] **Configurable search windows** (time-of-day, per-library)
- [ ] Fairness across large backlogs (no library starvation)
- [ ] Per-library search exclusion (skip this library in this run)
- [ ] Search scheduler UI with run history
- [ ] Max-items-per-run enforcement
- [ ] Search cost tracking (API calls, bandwidth)

**Effort:** 2 weeks | **Blocker:** Soft blocker - basic automation works, scheduling incomplete

### 8. Multi-Library & Root-Folder Support
**Status:** Partial - libraries exist, full multi-lib workflows incomplete  
**Impact:** MEDIUM - Power users have multiple collections  
**Work:**
- [x] Multiple libraries per media type (Movies A, Movies B)
- [x] Library-specific root folders
- [ ] **Bulk operations across libraries** (bulk monitor, bulk search, bulk assign profile)
- [ ] Library-specific automation settings (separate search intervals, quality targets)
- [ ] Cross-library duplicate detection
- [ ] Bulk move/reorg operations with safety checks
- [ ] Export/import library configuration

**Effort:** 2 weeks | **Blocker:** Soft blocker - single library works, multi-lib incomplete

### 9. Settings & Notifications
**Status:** Partial - basic settings exist, integrations missing  
**Impact:** MEDIUM - Users cannot be notified of issues  
**Work:**
- [x] App settings (paths, defaults, runtime preferences)
- [x] Quality profiles and custom formats UI
- [ ] **Notification integrations** (Discord, Pushbullet, email, webhooks)
- [ ] Activity logging and audit trail
- [ ] System health dashboard (storage, database, service status)
- [ ] Backup and restore UI
- [ ] Settings export/import
- [ ] Log level and diagnostics configuration

**Effort:** 2-3 weeks | **Blocker:** Soft blocker - no notifications is limiting

### 10. Real Search Result Ranking & Explanation
**Status:** Partial - basic ranking exists, explanation incomplete  
**Impact:** MEDIUM - Users don't understand why releases are grabbed  
**Work:**
- [x] Release scoring (source, quality, custom format score)
- [ ] **Decision explanation trail** - why this release vs. that one
- [ ] Indexer priority and routing logic visibility
- [ ] Custom format contribution breakdown (which formats matched, score impact)
- [ ] Cutoff and quality-ceiling enforcement explanation
- [ ] Release rejection reasons (too low quality, wrong language, etc.)

**Effort:** 1-2 weeks | **Blocker:** Soft blocker - ranking works, UX explanation incomplete

## TIER 3: Quality & Polish (v1.0+ Hardening)

Features that make the product professional and reliable but can be deferred past initial ship.

### 11. Realtime & SignalR Event Coverage
**Status:** Partial - SignalR wired, event coverage incomplete  
**Impact:** MEDIUM - No live UI updates, page reloads required  
**Work:**
- [x] SignalR hub registration at `/hubs/deluno`
- [ ] **Queue/import state change events** (currently no events, must poll)
- [ ] Search run progress updates
- [ ] Import completion and failure notifications
- [ ] Health status change broadcasts
- [ ] Activity timeline real-time updates
- [ ] Download client telemetry stream (not snapshot-only)

**Effort:** 2 weeks | **Blocker:** No, but UX will feel sluggish without this

### 12. Agent Readiness & Observability
**Status:** In progress - agent map exists, mechanical checks incomplete  
**Impact:** MEDIUM - Agents will struggle without this  
**Work:**
- [x] AGENTS.md and compact repo entry point
- [x] Architecture documentation (ARCHITECTURE.md, QUALITY_SCORE.md)
- [ ] **Local boot/health scripts** - produce agent-readable logs and URLs
- [ ] Architecture validation for project references and duplicated strings
- [ ] Persisted telemetry snapshots for agent consumption
- [ ] Execution-plan templates and discipline
- [ ] Automated quality drift detection

**Effort:** 2 weeks | **Blocker:** Soft blocker - agents can work, will be slower

### 13. Metadata & Fallback Handling
**Status:** Partial - TMDb exists, fallback incomplete  
**Impact:** MEDIUM - Missing metadata makes app frustrating  
**Work:**
- [x] TMDb metadata fetching (movie and TV)
- [ ] **Fallback chains when metadata is unavailable**
- [ ] Poster/artwork caching and serving
- [ ] Offline metadata handling (work with cached data)
- [ ] Alternative metadata sources (OMDB, TheTVDB fallback)
- [ ] Metadata refresh scheduling
- [ ] Custom metadata override UI

**Effort:** 2 weeks | **Blocker:** No, TMDb works, fallbacks are nice-to-have

### 14. UI Consistency & UX Polish
**Status:** In progress - premium surface exists, consistency incomplete  
**Impact:** MEDIUM - Users expect high visual quality  
**Work:**
- [x] Dense premium surface already exists
- [ ] **Typography and spacing consistency** across all pages
- [ ] Menu and dropdown behavior standardization
- [ ] Route loading and skeleton consistency
- [ ] Empty state messaging and affordances
- [ ] Form validation and error messaging
- [ ] Accessibility (WCAG 2.1 AA)
- [ ] Dark mode refinement and theme customization

**Effort:** 3 weeks | **Blocker:** No, but lack of polish will frustrate users

### 15. Bulk Operations & Workflows
**Status:** Partial - single-item workflows exist, bulk incomplete  
**Impact:** MEDIUM - Power users need bulk actions  
**Work:**
- [x] Single-item operations (monitor, set profile, assign tag)
- [ ] **Bulk monitor/unmonitor** with confirmation
- [ ] **Bulk set quality profile** across selections
- [ ] **Bulk assign root folder** for reorganization
- [ ] **Bulk apply tags** for organization
- [ ] **Bulk search** (now/manual, not just recurring)
- [ ] **Bulk rename** with preview
- [ ] Undo/redo for bulk operations

**Effort:** 2 weeks | **Blocker:** No, but bulk operations are high-value

## TIER 4: Advanced Features (Post-MVP)

Features that unlock power-user and advanced-automation scenarios. Defer unless explicitly requested.

### 16. Import Lists & Watchlist Sync
**Status:** Not started  
**Impact:** MEDIUM - Users want automated feed ingestion  
**Work:**
- Import list providers (Trakt, TMDB lists, IMDb watchlists)
- Per-source filtering (genre, age, rating, certification, audience)
- Automatic routing into correct library
- Sync scheduling and deduplication
- Clear skip reasons and diagnostics

**Effort:** 3-4 weeks

### 17. Monitoring & Alerting
**Status:** Not started  
**Impact:** LOW - Nice-to-have for ops  
**Work:**
- System health dashboard (storage, database, services)
- Performance metrics (search time, import time, API latency)
- Alerting rules (storage low, services down, error rates)
- Metrics export (Prometheus, InfluxDB)
- Log aggregation and search

**Effort:** 3 weeks

### 18. API Documentation & Webhooks
**Status:** Partially started  
**Impact:** MEDIUM - Power users and integrators need this  
**Work:**
- OpenAPI/Swagger documentation
- Webhook event types and payload documentation
- Webhook delivery and retry logic
- API key management UI
- Rate limiting and usage tracking

**Effort:** 2 weeks

### 19. Machine Learning & Intelligent Routing
**Status:** Not started  
**Impact:** LOW - Nice-to-have for advanced users  
**Work:**
- Learn user quality/format preferences from import history
- Predict best indexer and download client per release
- Anomaly detection (unusual grab patterns)
- Release recommendation engine

**Effort:** 4+ weeks (research-heavy)

## Cross-Cutting Improvements

### A. Testing Coverage
**Current:** 22 backend test files, web smoke tests  
**Needed:**
- Unit tests for release decision engine and scoring
- Integration tests for end-to-end import flows
- Contract tests for API clients (mock external services)
- E2E tests for critical user workflows (add movie, search, import)

**Effort:** Ongoing (2-3 weeks per major feature)

### B. Database Schema Stability
**Current:** SQLite with migrations, 5 separate databases (platform, movies, series, jobs, cache)  
**Needed:**
- Schema validation tests (prevent breaking changes)
- Migration rollback testing
- Backup/restore procedures documented
- Upgrade path testing

**Effort:** 1-2 weeks

### C. Performance & Scalability
**Current:** Single-user app, SQLite (adequate for single user)  
**Needed:**
- Query performance monitoring (identify N+1 queries)
- Background job concurrency limits
- Search result pagination (handle 10K+ results)
- Metadata caching strategy
- Index coverage for common filters

**Effort:** Ongoing (1-2 weeks per bottleneck)

### D. Documentation
**Current:** Agent-friendly docs exist (AGENTS.md, ARCHITECTURE.md, QUALITY_SCORE.md)  
**Needed:**
- User documentation (help for non-technical users)
- Deployment guide (Docker, Windows, Linux)
- Troubleshooting guide (common issues and solutions)
- API documentation (for integrators)
- Configuration reference

**Effort:** 2-3 weeks

## Implementation Order (Recommended Sequence)

### Phase 1: Stability & Core Flows (Weeks 1-4)
1. **Real integration plumbing** (download outcomes, history tracking)
2. **Episode-level TV workflows** (episode operations, season packs)
3. **Movie safety** (upgrade-only with custom format scoring)

**Why this order:** These three items unblock the entire product. Without them, Deluno is just a UI. Once these work, the product is credible.

### Phase 2: User-Facing Completeness (Weeks 5-8)
4. **Guide-backed presets** (quality/format templates, no YAML)
5. **Import recovery** (cleanup policies, re-import workflows)
6. **Search automation** (scheduling, fairness, backlog limits)

**Why this order:** Users can now avoid external tools. Phase 2 makes Deluno a complete replacement.

### Phase 3: Professional Finish (Weeks 9-12)
7. **Realtime events** (live UI updates, no polling)
8. **Settings & notifications** (Discord, email, webhooks)
9. **UI polish** (consistency, empty states, accessibility)

**Why this order:** Phase 3 makes the product feel premium and complete.

### Phase 4: Advanced Features (Weeks 13+)
10. **Bulk operations** (power-user workflows)
11. **Import lists & watchlist sync** (Fetcher replacement)
12. **Monitoring & alerting** (ops visibility)

## Risk Assessment

### High Risk
- **Episode import resolution** - complex multi-episode logic, easy to create edge cases
- **Download client integration** - external service deps, network reliability
- **Custom format scoring** - user expectation mismatches if logic is opaque

### Medium Risk
- **Search scheduling fairness** - can starve large backlogs if not careful
- **Metadata fallback chains** - incomplete fallbacks will frustrate users
- **SignalR stability** - connection drops and reconnection logic
- **Database schema evolution** - breaking migrations will require user data recovery

### Low Risk
- **UI polish** - incremental, no product risk
- **Notifications** - failure doesn't break core features
- **Bulk operations** - can be deferred without blocking MVP

## Quality Metrics

Current state (2026-05-09):
- Overall grade: **B-**
- Backend tests: 22 files
- Frontend routes: 30 pages
- API endpoints: 60+ endpoints
- Codebase: 203 C# files, 92 TypeScript/TSX files

Target for v1.0:
- Overall grade: **A-** (high-quality, complete, reliable)
- Backend tests: 40+ files (add decision logic, import flow, integration tests)
- API endpoints: 80+ endpoints (bulk operations, import lists, webhooks)
- Test coverage: 70%+ for core domains (movies, series, imports, search)
- E2E test coverage: 10+ critical workflows

## Success Criteria for MVP (v1.0)

Users should be able to:
1. ✅ Add and monitor movies and TV shows
2. ✅ Set quality profiles and custom formats with safe presets
3. ⬜ Configure indexers and download clients with real connections
4. ⬜ Watch recurring missing/upgrade searches happen automatically
5. ⬜ Import files with safe rename and replacement protection
6. ⬜ Fix failed imports without leaving Deluno
7. ⬜ Understand why releases were grabbed (or not)
8. ⬜ Receive notifications of important events

Estimated timeline to MVP: **12-14 weeks** (assuming 1 senior engineer)

---

## How to Use This Roadmap

- **For prioritization:** Follow the Tier 1 → Tier 2 → Tier 3 → Tier 4 sequence
- **For agents:** Use this to understand what's in scope and why
- **For status tracking:** Update section status as work progresses
- **For risk management:** Reference the risk assessment before starting large items
