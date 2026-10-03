# Deluno Agent Map

This file is the small entry point for agent work. It is a map, not the full manual.

## First Checks

- Work from `C:\Projects\Deluno`.
- Do not use `C:\Users\User\Deluno`.
- Run `git status --short --branch` before editing.
- Use `rg` for search.
- Use `apply_patch` for manual edits.
- Stage explicit paths only.
- Work on `main` itself: no feature branches and no pull requests. One person
  works on Deluno (James, 22 September 2026), so commit to `main`, run the
  gates, and push. A branch found lying around is a mistake: fold it into
  `main` or delete it, locally and on GitHub.

## Source Of Truth

- **Closing issues: `docs/exec-plans/active/BACKLOG-SWEEP.md`.** Run
  `./scripts/issue-audit.ps1 <number>` before touching an issue and answer every
  acceptance line. The list is the whole contract; do not add to it. The burden
  is on leaving an issue open, not on closing it.
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

Run the smallest relevant checks while working. Before the first push of a batch, run the full gate. If one step then
fails and the fix stays inside one area, re-run only the steps that fix can affect: `npm run test:web` for a web-only
fix, a Release build plus the affected test projects for a backend-only fix. Run the whole gate again only when a fix
crosses areas: backend and web, a migration, or a shared contract (James, 3 Oct 2026). The full gate is:

```powershell
npm run gate
```

It builds Release once, then runs the backend tests, `npm run ci:check` and
`npm run test:web` side by side (about 8 minutes instead of about 20 one after
another), and says which step failed with the tail of its log.

Changes land on `main` directly, and each push runs CI once (`ci.yml`). Every
run costs Actions minutes, so batch the work: push once, after the local gates
and, for anything the rig can show, the rig proof. The account is on GitHub
Pro (James, 3 Oct 2026), so branch protection and rulesets are available on
this private repository, but none is set up yet: until it is, nothing on GitHub
guards `main`. Collaborators on a personal repository can only hold Write
access, so any collaborator could push to `main` or push a `v*` tag, and a tag
publishes a public container image and GitHub release. A local pre-push hook is
available; enable it once with:

```powershell
git config core.hooksPath .githooks
```

## Mechanical Guardrails

- **Never write `close`, `fixes` or `resolves` next to an issue reference in a
  PR body or commit message unless you mean it.** GitHub's parser does not read
  negation, and does not care that you are quoting: `does not close #NNN` closes
  issue NNN, and a markdown-linked or full-URL reference counts the same. Write
  `Refs #NNN`, and state the scope in a sentence that keeps the number away from
  any of those verbs — `#NNN stays open for X`. Never spell the bad pattern with
  a real issue number, not even as an example. This rule cost three accidental
  closures in one session: two from PRs that said in the same paragraph they were
  not finishing the issue, and one from the commit message that first wrote this
  guardrail down using a live number.
- Keep docs discoverable from this map or `docs/README.md`.
- Add or update tests when changing normalized contracts, status strings, routing, import behavior, or persisted schemas.
- Prefer shared helpers for status, capability, and routing invariants over duplicated string checks.
- If an agent struggles, improve the repository map, validation script, tests, or docs instead of relying on handover text.
