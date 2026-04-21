# Deluno Frontend and Backend Architecture

## Locked Decision

Deluno is locked to this stack:

- `Frontend`: React 19 + React Router v7 + Vite + TypeScript
- `Backend`: ASP.NET Core 10
- `Storage`: SQLite only
- `Realtime`: SignalR
- `Workers`: hosted background workers with durable DB-backed job state

This is the best architecture for the product we are building:

- home-user first
- Windows first-class
- Docker first-class
- single-app deployment
- high operational complexity
- strict movie and series separation

PostgreSQL is not part of the core Deluno architecture.

## Product Priorities

The architecture is optimized for:

- simplest possible install for home users
- best performance on one machine
- no extra required services
- strong isolation between movie and series logic
- clean upgrade path without future rewrites

The goal is not just to be "good enough." The goal is to be better than the current split-app experience.

## Frontend Stack

Use:

- `React 19`
- `React Router v7`
- `Vite`
- `TypeScript`
- `React Compiler`

### Why this is the best frontend

React recommends using a framework for full apps, and React Router is one of the officially recommended framework paths for production React apps. React Router also works well for a Backend-For-Frontend style setup where the browser talks directly to the backend API through route loaders and actions. Vite remains the best fit for fast local development and fast iteration.

Sources:

- [React: Creating a React App](https://react.dev/learn/creating-a-react-app)
- [React Router: Client Data](https://reactrouter.com/how-to/client-data)
- [Vite: Why Vite](https://vite.dev/guide/why.html)
- [React Compiler v1.0](https://react.dev/blog/2025/10/07/react-compiler-1)

### Frontend runtime model

Deluno should be:

- SPA-first
- served as static assets by the ASP.NET Core backend in production
- free of any required Node server in production

SSR is optional later for:

- a public landing page
- a setup screen
- a future marketing surface

It is not part of the core app runtime.

### Frontend architecture rules

- Route-first architecture with React Router
- Route loaders/actions as the default server-data mechanism
- URL state for filters, sort, search, and paging
- SignalR for live queue and activity updates
- plain React state and reducers for local UI state
- no workflow/business decisions in the frontend

Recommended top-level routes:

- `/`
- `/movies`
- `/movies/:movieId`
- `/series`
- `/series/:seriesId`
- `/activity`
- `/calendar`
- `/wanted`
- `/history`
- `/settings`

## Backend Stack

Use:

- `ASP.NET Core 10`
- `SignalR`
- `BackgroundService`
- `OpenTelemetry`
- `SQLite`
- `Dapper` for hot paths
- optional targeted `EF Core` only where it clearly helps

### Why this is the best backend

ASP.NET Core directly fits Deluno's workload:

- background services for long-running work
- SignalR for realtime updates
- rate limiting middleware
- strong observability support through OpenTelemetry

Sources:

- [ASP.NET Core hosted services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0)
- [ASP.NET Core SignalR](https://learn.microsoft.com/en-us/aspnet/core/signalr/introduction?view=aspnetcore-10.0)
- [ASP.NET Core rate limiting](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0)
- [.NET OpenTelemetry observability](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-with-otel)

The backend is not just a CRUD API. It has to handle:

- scheduled searches
- download polling
- imports
- file operations
- release matching
- retries
- progress updates
- external integrations

That is why ASP.NET Core wins over a Node-first backend here.

## Storage Architecture

SQLite is the only core database technology for Deluno.

### Why SQLite only

For Windows and Docker home users, the best product is the one that:

- starts easily
- ships as one app
- does not require a separate database server
- can be backed up by copying files
- works identically in local installs and single-container installs

PostgreSQL would add packaging, support, and operational complexity that works against the product goal.

### Multiple database files, not one

Do not use one giant SQLite file.

Use multiple SQLite databases with hard ownership boundaries:

- `platform.db`
- `movies.db`
- `series.db`
- `jobs.db`
- `cache.db`

This gives Deluno:

- lower write contention
- cleaner module ownership
- safer isolation between movie and series workflows
- simpler recovery and inspection

SQLite allows many readers but only one writer per database file. Splitting the data across multiple files is the right performance and architecture decision for Deluno.

### Database ownership

`platform.db`

- users
- settings
- API keys
- notifications
- audit log
- health snapshots

`movies.db`

- movie catalog
- movie monitoring
- movie profiles
- movie history
- movie imports

`series.db`

- shows
- seasons
- episodes
- series monitoring
- series profiles
- series history
- series imports

`jobs.db`

- scheduled jobs
- job runs
- leases
- attempts
- dead letters
- worker heartbeats

`cache.db`

- provider payload cache
- artwork cache metadata
- transient normalization data

### SQLite performance rules

- enable WAL mode
- keep write transactions short
- never hold long import transactions open
- use prepared statements
- index for real lookup paths
- prefer append-only history/event tables
- aggregate across modules in application code instead of relying on cross-db relational joins

## Module Isolation

Deluno should have full internal separation between major modules.

Locked module set:

- `Platform`
- `Movies`
- `Series`
- `Jobs`
- `Integrations`
- `Realtime`
- `Filesystem`

### Isolation rules

- each module owns its own data
- each module owns its own job types
- each module exposes explicit contracts
- `Movies` never reads `Series` persistence directly
- `Series` never reads `Movies` persistence directly
- no shared movie/series domain entities
- no shared business-rule package across media types

The UI can present one coherent app while the internals remain fully separated.

## Worker Architecture

Deluno should run with one app by default, but internally partition work aggressively.

Recommended worker partitions:

- movie search worker
- series search worker
- movie import worker
- series import worker
- download poll worker
- metadata refresh worker
- cleanup worker

This keeps:

- noisy movie work from blocking series work
- series edge cases from destabilizing movie pipelines
- import workloads isolated by media type

### Job execution model

Jobs should be durable, not timer-only.

Execution flow:

1. a scheduler finds due jobs in `jobs.db`
2. a worker acquires a lease
3. the worker executes the job
4. status and history are written
5. an event is emitted
6. SignalR pushes updates to clients

## API and Realtime

Use:

- JSON HTTP API for core operations
- SignalR for live state and progress

Recommended API families:

- `/api/platform/*`
- `/api/movies/*`
- `/api/series/*`
- `/api/integrations/*`
- `/api/activity/*`

Use SignalR for:

- activity feed updates
- job progress
- import progress
- search progress
- notifications
- health changes

The database remains the source of truth. SignalR is the live-update channel.

## Packaging

### Windows

Optimize for:

- self-contained publish
- single-file distribution where practical
- optional Windows Service mode
- local writable app data folder containing the SQLite files

Relevant .NET deployment guidance:

- [Application publishing overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/index)

### Docker

Optimize for:

- one-container default install
- bind mounts for config, downloads, and media
- no required companion database container
- optional advanced split mode later if needed

Relevant .NET container guidance:

- [Official .NET Docker images](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/net-core-net-framework-containers/official-net-docker-images)
- [Containerize a .NET app with Docker](https://learn.microsoft.com/en-us/dotnet/core/docker/build-container)

## What We Are Explicitly Avoiding

- PostgreSQL as a required dependency
- a separate Node server in production
- microservices
- GraphQL-first design
- one shared SQLite file
- merged movie/series business logic
- frontend-owned workflow rules
- polling as the primary live-status mechanism

## Canonical Build Direction

If we scaffold Deluno from this decision, the implementation shape should be:

- `apps/web`: React Router frontend
- `src/Deluno.Host`: ASP.NET Core host
- `src/Deluno.Api`: API endpoints
- `src/Deluno.Worker`: worker runtime
- `src/Deluno.Platform`
- `src/Deluno.Movies`
- `src/Deluno.Series`
- `src/Deluno.Jobs`
- `src/Deluno.Integrations`
- `src/Deluno.Realtime`
- `src/Deluno.Filesystem`
- `src/Deluno.Infrastructure`

This is the locked architecture unless we discover something concrete in implementation that beats it on:

- home-user simplicity
- Windows packaging
- Docker packaging
- performance
- module isolation
