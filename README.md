# Deluno

Deluno is a clean-room media automation project aimed at delivering one app with two isolated engines:

- `Movies`
- `Series`

The product goal is to feel unified for users while preserving strict internal separation so movie and TV logic cannot interfere with each other.

## Current Focus

Right now the project is being built for speed, validation, and clean architecture:

- no monetization yet
- no dependency on copying Radarr or Sonarr code
- metadata providers abstracted from day one
- self-hosted first
- future commercialization kept possible, but not blocking progress

## Repo Shape

- `apps/api`: minimal runnable backend shell
- `apps/web`: frontend placeholder and future UI app
- `docs`: product, architecture, and execution docs
- `packages/platform`: app-level shared concerns only
- `packages/movies`: movie-only domain logic
- `packages/series`: series-only domain logic
- `packages/integrations`: metadata/indexer/download client abstractions

## Why This Shape

We want a fast start without creating future cleanup work.

That means:

- one repo
- one app shell
- isolated media domains
- provider abstraction now, not later
- no shared movie/series business logic

## Commands

Run the API shell:

```bash
node apps/api/src/server.js
```

Then open:

- `http://localhost:4000/health`
- `http://localhost:4000/api`
- `http://localhost:4000/api/domains`
- `http://localhost:4000/api/providers`

## Next Build Steps

1. Add persistent storage and migrations.
2. Implement metadata provider adapters.
3. Ship a movie vertical slice end to end.
4. Ship a series vertical slice end to end.
5. Add a real web UI shell.
