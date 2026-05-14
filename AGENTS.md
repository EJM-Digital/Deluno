# Deluno Agent Map

This file is the small entry point for agent work. It is a map, not the full manual.

## First Checks

- Work from `C:\Users\User\Projects\Deluno`.
- Do not use `C:\Users\User\Deluno`.
- Run `git status --short --branch` before editing.
- Use `rg` for search.
- Use `apply_patch` for manual edits.
- Stage explicit paths only.

## Source Of Truth

- Product scope: `docs/deluno-capability-map.md`
- Architecture boundaries: `docs/ARCHITECTURE.md`
- Frontend/backend map: `docs/deluno-frontend-backend-map.md`
- API contracts: `docs/deluno-ui-api-contract.md`
- External integrations: `docs/external-integration-api.md`
- Metadata broker: `docs/metadata-broker-contract.md`
- Quality score and gaps: `docs/QUALITY_SCORE.md`
- GA release checklist and sign-off flow: `docs/ga-release-checklist.md`
- Active execution plans: `docs/exec-plans/active/`

## Current Product Direction

- Deluno orchestrates external indexers and download clients; it does not embed a downloader.
- The app is single-user. Avoid operator/admin/team-language unless referring to platform APIs or accessibility attributes.
- Movie and TV engines stay separated internally, even when UI workflows are unified.
- Services/Broker, Queue, Activity, Health, and Imports should consume normalized client/indexer data.
- Refine-before-import remains first-class: download completes, processor cleans output, Deluno imports the clean output through the same resolver.

## Validation

Run the smallest relevant checks while working. Before every push, run the CI gate:

```powershell
npm run ci:check
```

Before merging, also run the full test suite:

```powershell
dotnet test Deluno.slnx --configuration Release
npm run test:web
```

## Mechanical Guardrails

- Keep docs discoverable from this map or `docs/README.md`.
- Add or update tests when changing normalized contracts, status strings, routing, import behavior, or persisted schemas.
- Prefer shared helpers for status, capability, and routing invariants over duplicated string checks.
- If an agent struggles, improve the repository map, validation script, tests, or docs instead of relying on handover text.
