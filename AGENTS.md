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
  is on leaving an issue open, not on closing it. A UI or behaviour issue closes
  only with live proof on the rig: what was done and what was seen, on which
  build, written in the closing note.
- **The bar, and the standing check every change is measured against: `docs/PRODUCT_NORTH_STAR.md`.** Read it first. It records what Deluno replaces and what "better, not merely present" means; it is where [#194](https://github.com/jampat000/Deluno/issues/194) lives now that the issue is closed.
- What each top-level folder is: the Repository map in `docs/README.md`
- Product scope: `docs/deluno-capability-map.md`
- Architecture boundaries: `docs/ARCHITECTURE.md`
- Frontend/backend map: `docs/deluno-frontend-backend-map.md`
- API contracts: `docs/deluno-ui-api-contract.md`
- External integrations: `docs/external-integration-api.md`
- Deluno-Weir contract (every call, reply and report, with its recording and test): `docs/deluno-weir-contract.md`
- Metadata broker: `docs/metadata-broker-contract.md`
- Quality score and gaps: `docs/QUALITY_SCORE.md`
- Coverage register (every behaviour and the tests that prove it; add a row with every new route, job, migration or screen): `docs/testing/COVERAGE.md`
- CI and release standard (shared with Weir): `docs/ci-standard.md`
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

It builds Release once, then runs the backend tests, `npm run ci:check`, the web
unit tests and the browser smoke side by side (about 8 minutes instead of about
20 one after another), and says which step failed with the tail of its log. The
gate runs the smoke in CI's two shards (`--shard=1/2` and `2/2`), each against its
own fresh smoke host and database on its own ports, so a spec that depends on
state an earlier spec left behind fails before the push, not in CI.

Changes land on `main` directly, and each push runs CI once (`ci.yml`). Every
run costs Actions minutes, so batch the work: push once, after the local gates
and, for anything the rig can show, the rig proof. CI is path-filtered: a push
that changes only documents runs only the repository checks, a web-only change
skips the backend build, tests and image, and a backend change also runs the
browser smoke tests. A manual run (`gh workflow run ci.yml`) runs everything.
Its last job, `ci-passed`, is the one verdict: it is the single check branch
protection will require (`docs/ci-standard.md` is the standard, shared with
Weir). Run `node scripts/check-github-action-pins.mjs`, `node scripts/check-workflow-hygiene.mjs`
and `node scripts/check-release-workflow-gates.mjs` after touching a workflow;
`npm run ci:check` runs them too. Every action is pinned to a commit SHA with a
release comment, every workflow has `permissions` and every job a timeout.

## Releases

- **A tag is made only after CI is green on that commit** (James, 3 Oct 2026).
  The release's first job checks it (`scripts/verify-ci-for-release.mjs`) and
  does not wait: tag while CI is still running and the release fails at once.
  A green push run on a documents-only commit proves little, so tag a commit
  whose own run ran the jobs, or run `ci.yml` by hand on it first.
- **Tags are made only through `scripts/tag-release.ps1 -Version <x.y.z[-rc.N[.M]]>`** (9 Oct 2026). It
  refuses unless the working tree is clean, HEAD is `origin/main`, `Directory.Build.props` and
  `RELEASE_NOTES.md` carry the version, CI passed on HEAD and ran the jobs, and the golden path passed
  on HEAD. Then it creates and pushes the annotated tag. Never run `git tag` for a release by hand.
- **Right the first time** (James, 11 Oct 2026). A fix is done only when a failing test first reproduced it from real evidence (a log, a recorded reply, a screen record); tests of another app use its recorded real replies (`tests/fixtures/recorded/`), never an assumed shape; and the acceptance lines name every variation (each client, each timing, with and without work in flight), each tested.
  The scenario suite on the golden VM proves the candidate before the tag, and every run keeps Deluno's and Weir's logs.
- **A tag is made only after the scenario suite has passed on that exact commit** (James, 7 and 11 Oct 2026;
  `docs/golden-path.md`, issues 1158 and 1238). No waiver, release candidates included. Build the commit's installer with a manual
  `ci.yml` run (`windows-installer`), then run `./scripts/golden-path/Invoke-ScenarioSuite.ps1 -RigHost <host> -InstallerPath <Deluno-rc-Setup.exe> -Version <x.y.z[-rc.N]> -Commit <sha>`
  (unattended, about two hours: 124 minutes in the rc.30.29 run; the per-group times are in `docs/golden-path.md`). It keeps Deluno's and Weir's full logs and records the `golden-path` commit status itself: `success` on a
  full pass, `failure` naming the failed scenarios otherwise. The release's `golden-path` job (`scripts/verify-golden-path-for-release.mjs`)
  passes only when the newest `golden-path` status on the tagged commit is `success`, and does not wait.
  After a fix, check only the scenario that needed it: `-Scenarios <id,id>` runs single scenarios, and `-UseReadyCheckpoint` skips the
  install and setup of a commit already saved as a ready checkpoint. Such a run records no status and is never the run that gates a tag
  (James, 14 Oct 2026).
- **One workflow, `release.yml`, publishes, in two independent jobs.** Both need
  `ci-passed`, `golden-path` and `validate`, plus their own build, and neither
  waits for the other. `publish-windows` (needs `windows`) alone has
  `contents: write` and creates the GitHub Release. `publish-docker` (needs
  `docker-candidate`) alone has `packages: write` and the registry login; it
  pushes the image, attests, checks and starts it, and moves the moving image
  tags as its last step, never for a release candidate. Either can be re-run
  alone (`gh run rerun --job`). There is no separate Docker publish workflow
  (`docker.yml` is gone), so the image can no longer go public for a release
  whose checks failed. `scripts/check-release-workflow-gates.mjs` enforces the shape.
- After tagging, confirm the Release run is green, the GitHub Release has its
  assets and the image pulls by its version tag before saying it is released.
- The account is on GitHub Pro (James, 3 Oct 2026), so branch protection and
  rulesets are available on this private repository; require the `ci-passed`
  check on `main` and restrict who can create a `v*` tag. Until that is set up,
  nothing on GitHub guards `main`, and any collaborator (Write access is the
  most a personal repository gives) could push to it or push a tag, which
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
