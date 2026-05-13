# Deluno Architecture

Updated: 2026-05-13

Deluno is a single-user media automation app with separated movie and TV engines, external service orchestration, durable local state, and a growing operations layer around search, imports, health, and recovery.

## Stable Module Map

- `Deluno.Host`: composition root, endpoint registration, static frontend hosting.
- `Deluno.Api`: host-level API concerns and readiness.
- `Deluno.Platform`: settings, bootstrap, libraries, quality profiles, tags, API keys, routing, and the expanding app-services layer.
- `Deluno.Movies`: movie catalog, wanted state, search, grabs, metadata actions, and import recovery.
- `Deluno.Series`: series catalog, episode state, wanted state, search, grabs, metadata actions, inventory, and import recovery.
- `Deluno.Integrations`: indexers, metadata adapters, download clients, telemetry, grabs, webhooks, and normalized external orchestration.
- `Deluno.Jobs`: durable queue, activity, search-cycle memory, and background work state.
- `Deluno.Filesystem`: import planning, media probing, transfer policy, and recovery helpers.
- `Deluno.Realtime`: SignalR events and hub wiring.
- `Deluno.Infrastructure`: storage, resilience, observability support, and runtime infrastructure.
- `Deluno.Worker`: hosted background orchestration.
- `Deluno.Contracts`: shared low-level contracts only.

## In-Flight Supporting Modules

The current working tree also contains early supporting namespaces that reflect direction more than settled ownership:

- `Deluno.Library`: quality and episode-workflow service contracts
- `Deluno.Search`: automation, health, and ranking service contracts

These should be treated as in-flight seams until they are either:

- adopted as stable modules with wiring and tests
- or folded back into existing domain modules with clearer ownership

## Ownership Direction

### Platform

Platform is no longer just generic settings storage.

It now owns or is growing into:

- authentication and bootstrap
- libraries and routing
- quality profiles, tags, custom formats, intake sources, policy sets, and destination rules
- migration import flows
- system health/log/job surfaces
- analytics, cleanup, explanations, idempotency, observability, presets, resilience, and settings services

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

### Jobs, Realtime, And Operations

Operational visibility is split intentionally:

- `Deluno.Jobs` owns durable state for queue and activity
- `Deluno.Realtime` owns live event delivery
- `Deluno.Platform` increasingly owns the higher-level operational APIs and orchestration surfaces

## Boundary Rules

- Movies and Series do not reference each other.
- Integrations stays domain-neutral and should not reference Movies, Series, or Filesystem directly for business logic.
- Feature modules may depend on Platform, Jobs, Integrations, Infrastructure, or Contracts as needed.
- Shared behavior should move to a shared module instead of crossing movie/series boundaries.
- Host and Worker may compose modules, but should not become domain owners.
- Persistence schema changes require tests and a doc update in the relevant map, contract, or strategy file.

## Agent-Legible Invariants

- Deluno orchestrates external indexers and download clients; it does not embed a downloader.
- The app is single-user. Avoid operator/admin/team language unless an external API requires it.
- Movie and TV engines stay separated internally even when UI workflows are unified.
- Services/Broker, Queue, Activity, Health, and Imports should consume normalized client/indexer data rather than raw external payload quirks.
- Refine-before-import remains first-class: external processing can clean output, but Deluno still resolves destination and imports the final artifact.
- Status strings used by queues/imports/download clients should have one canonical home.
- Protocol support differences should be encoded as capability data, not scattered UI conditionals.
- External payloads should be parsed into typed contracts before business logic touches them.

## Current Architectural Risk

The biggest immediate architecture risk is not missing modules. It is concentration.

`src/Deluno.Platform/PlatformEndpointRouteBuilderExtensions.cs` has become a very large API owner. The contracts are clearer than the composition. Future refactors should separate route registration and service ownership by concern without collapsing boundaries between domains.

## Validation Hooks

- `npm.cmd run validate:agents` checks documentation and high-signal architecture guardrails.
- `.\\.dotnet\\dotnet.exe test .\\Deluno.slnx --configuration Release` checks backend contracts and persistence behavior.
- `npm.cmd run build:web` checks frontend type and route integrity.
- `npm.cmd run test:web` checks browser smoke coverage.
