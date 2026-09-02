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

- **The bar, and the standing check every change is measured against: `docs/PRODUCT_NORTH_STAR.md`.** Read it first. It records what Deluno replaces and what "better, not merely present" means; it is where [#194](https://github.com/jampat000/Deluno/issues/194) lives now that the issue is closed.
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

- Deluno orchestrates external indexers and download clients. It does not embed a transfer engine; external clients remain responsible for protocol work, queueing, repair, unpacking, retention, and seeding.
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

Changes reach `main` through a pull request. Wait for every required GitHub
check to pass, then manually squash-merge; never use auto-merge. `main` **is**
branch-protected and asks for one approving review, so a solo squash-merge
needs `gh pr merge --squash --admin` (admin enforcement is off for exactly
this). This paragraph used to say the branch was unprotected, which was true
once and stopped being true without anyone noticing. A local pre-push hook is
available; enable it once with:

```powershell
git config core.hooksPath .githooks
```

## Mechanical Guardrails

- **Never write `close`, `fixes` or `resolves` next to an issue reference in a
  PR body or commit message unless you mean it.** GitHub's parser does not read
  negation: "This does not close #354" closes #354, and a linked or full-URL
  reference counts too. Write "Refs #354" and say the scope in a separate
  sentence — "#354 stays open for X". Two issues were closed this way in one
  session, both in PRs that said in the same paragraph they were not finishing
  the issue.
- Keep docs discoverable from this map or `docs/README.md`.
- Add or update tests when changing normalized contracts, status strings, routing, import behavior, or persisted schemas.
- Prefer shared helpers for status, capability, and routing invariants over duplicated string checks.
- If an agent struggles, improve the repository map, validation script, tests, or docs instead of relying on handover text.
