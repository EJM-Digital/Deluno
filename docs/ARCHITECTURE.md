# Deluno Architecture

Updated: 2026-08-21

Deluno is a single-user media automation app with separated movie and TV engines, external service orchestration, durable local state, and a growing operations layer around search, imports, health, and recovery.

## Stable Module Map

- `Deluno.Host`: composition root, endpoint registration, static frontend hosting.
- `Deluno.Api`: host-level API concerns and readiness.
- `Deluno.Platform`: shared settings, bootstrap, tags, migration, health, logging, and cross-module application orchestration.
- `Deluno.Security`: authentication, users, API keys, authorization policies, and secret protection.
- `Deluno.Notifications`: notification preferences, webhook persistence, and outbound notification delivery.
- `Deluno.Intake`: intake sources, list exclusions and previews, and intake-origin contracts.
- `Deluno.Connections`: indexers, download clients, protocol metadata, and connection health.
- `Deluno.Libraries`: library definitions, routing, destination rules, library views, and import-run tracking.
- `Deluno.Quality`: quality profiles, custom formats, policy sets, presets, and quality decisions.
- `Deluno.Movies`: movie catalog, wanted state, search, grabs, metadata actions, and import recovery.
- `Deluno.Series`: series catalog, episode state, wanted state, search, grabs, metadata actions, inventory, and import recovery.
- `Deluno.Integrations`: indexers, metadata adapters, download clients, telemetry, grabs, webhooks, and normalized external orchestration.
- `Deluno.Jobs`: durable queue, activity, search-cycle memory, and background work state.
- `Deluno.Filesystem`: import planning, media probing, transfer policy, and recovery helpers.
- `Deluno.Realtime`: SignalR events and hub wiring.
- `Deluno.Infrastructure`: storage, resilience, observability support, and runtime infrastructure.
- `Deluno.Worker`: hosted background orchestration.
- `Deluno.Contracts`: shared low-level contracts only.

## Ownership Direction

### Platform

Platform owns shared application settings, bootstrap, tags, migration flows, system health and log surfaces, and
cross-module orchestration. Domain-specific ownership belongs in the modules below rather than returning to this
composition layer.

### Security

Security owns authentication, bootstrap-user and API-key contracts, authorization policies, rate limits, and the
secret-protection backends used for stored credentials. Other modules consume its authorization contracts; they do not
reimplement credential handling.

### Notifications

Notifications owns notification preferences, webhook records, notification event contracts, and outbound delivery.
Notification persistence remains in `platform.db` because it is app-level state, even though its code has a dedicated
module.

### Intake

Intake owns list sources, exclusions, previews, source validation, and the contracts used to turn external intake into
library candidates. It does not own movie or TV catalog state.

### Connections

Connections owns indexer and download-client configuration, protocol capability metadata, path mappings, and
connection-level health/test operations. It normalizes external connection data before Jobs, Movies, Series, or the UI
consume it.

### Libraries And Quality

Libraries owns library definitions, routing links, destination rules, library views, and import-run records. Quality
owns quality profiles, custom formats, policy sets, presets, and the decisions that apply them to candidate media.

### Movies And Series

Movies and Series remain separate engines internally.

They should continue to own:

- catalog state
- monitoring state
- wanted state
- metadata jobs and linking
- manual search and grab workflows
- import recovery workflows

They should not become thin wrappers around a merged media domain.

### Integrations

Integrations owns normalization of external protocols before higher layers consume them.

That now includes:

- indexer setup and tests
- download-client setup and tests
- normalized telemetry
- queue actions
- direct grabs
- webhook ingestion
- metadata provider fallback
- search/result scoring support

### Intake, Connections, Libraries, And Quality

These modules are shared application seams, not alternate media engines. They may be consumed by both Movies and Series,
but must not make either domain depend on the other:

- Intake prepares source and candidate data.
- Connections normalizes indexers and download clients.
- Libraries resolves routing and destination policy.
- Quality evaluates profiles, custom formats, and policy sets.

### Jobs, Realtime, And Operations

Operational visibility is split intentionally:

- `Deluno.Jobs` owns durable state for queue and activity
- `Deluno.Realtime` owns live event delivery
- `Deluno.Platform` increasingly owns the higher-level operational APIs and orchestration surfaces

## Boundary Rules

- Movies and Series do not reference each other.
- Security owns authentication and secret protection; feature modules consume its contracts.
- Notifications owns outbound notification delivery; feature modules publish typed events.
- Intake, Connections, Libraries, and Quality are shared seams and must remain domain-neutral.
- Integrations stays domain-neutral and should not reference Movies, Series, or Filesystem directly for business logic.
- Feature modules may depend on Platform, Jobs, Integrations, Infrastructure, or Contracts as needed.
- Shared behavior should move to a shared module instead of crossing movie/series boundaries.
- Host and Worker may compose modules, but should not become domain owners.
- Persistence schema changes require tests and a doc update in the relevant map, contract, or strategy file.

## Agent-Legible Invariants

- Deluno orchestrates external indexers and download clients. It does not embed a transfer engine; external clients remain responsible for protocol work, queueing, repair, unpacking, retention, and seeding.
- The app is single-user. Avoid operator/admin/team language unless an external API requires it.
- Movie and TV engines stay separated internally even when UI workflows are unified.
- Services/Broker, Queue, Activity, Health, and Imports should consume normalized client/indexer data rather than raw external payload quirks.
- Refine-before-import remains first-class: external processing can clean output, but Deluno still resolves destination and imports the final artifact.
- Status strings used by queues/imports/download clients should have one canonical home.
- Protocol support differences should be encoded as capability data, not scattered UI conditionals.
- External payloads should be parsed into typed contracts before business logic touches them.

## Current Architectural Risk

The remaining architectural risk is keeping the module seams explicit as the
composition root grows. The platform route surface is now separated into
settings/setup/tags, migration, library actions, and external integrations;
the platform settings repository is likewise separated from download health,
processor, and migration-audit persistence. The endpoint inventory test in
`tests/Deluno.Platform.Tests/Routing/` guards the route table while future seams
are moved. See `docs/exec-plans/active/ADR-001-module-boundaries.md` for the
boundary rules and ownership decisions.

## Storage And Write Capacity

Deluno uses five SQLite database files, each with its own writer: `platform.db`
owns settings, credentials, notifications, and audit; `movies.db` owns the movie
catalogue and import state; `series.db` owns shows, episodes, and import state;
`jobs.db` owns schedules, leases, activity, dispatches, and heartbeats; and
`cache.db` owns provider payloads and transient normalization artifacts. SQLite
serialises writes per file, not globally, and WAL with private connection caches
allows readers to run alongside that file's writer.

The opt-in `SqliteWriteThroughput` benchmark ran on 2026-08-21 from a temporary
data root on an ASUS system with an AMD Ryzen 7 9800X3D (16 logical processors),
61.7 GiB RAM, and a Samsung SSD 990 PRO 4TB on NTFS. Each result below is the
range across 1, 2, 4, 8, 16, and 24 concurrent writers during ten-second runs;
the synthetic table uses the same connection factory and WAL/private-cache
settings as the application. Every run recorded zero `SQLITE_BUSY` or
`SQLITE_LOCKED` errors.

| Database | One row per transaction | 100 rows per transaction | Highest p99 commit latency |
| --- | ---: | ---: | ---: |
| `platform.db` | 39,207–44,469 rows/s | 285,300–355,790 rows/s | 2.533 ms |
| `movies.db` | 38,350–42,578 rows/s | 298,700–367,680 rows/s | 2.578 ms |
| `series.db` | 38,752–43,163 rows/s | 281,210–363,300 rows/s | 2.560 ms |
| `jobs.db` | 38,804–42,176 rows/s | 293,170–355,560 rows/s | 2.562 ms |
| `cache.db` | 31,278–33,240 rows/s | 264,200–301,240 rows/s | 10.297 ms |

The measurement does not justify a sixth `dispatches.db`: `jobs.db` sustained
roughly 39–42k single-row writes/s with no lock pressure, and this repository
has no observed production peak within the issue's three-times decision threshold
that would justify cross-database joins. The five-file layout stays in place;
the activity writer now batches events in one transaction, while measured hot
read paths can use read-only connections.

## Validation Hooks

- `npm.cmd run validate:agents` checks documentation and high-signal architecture guardrails.
- `.\\.dotnet\\dotnet.exe test .\\Deluno.slnx --configuration Release` checks backend contracts and persistence behavior.
- `npm.cmd run build:web` checks frontend type and route integrity.
- `npm.cmd run test:web` checks browser smoke coverage.
