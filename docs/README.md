# Deluno Knowledge Base

Deluno keeps product, architecture, and repo-state context in the repository so agents can inspect and update it directly.

## Start Here

- `../AGENTS.md`: compact agent entry point.
- `ARCHITECTURE.md`: module boundaries, dependency direction, and current architectural posture.
- `QUALITY_SCORE.md`: current quality posture and highest-interest gaps.
- `testing/STRATEGY.md`: the testing strategy: what 100% coverage means (seven measures, each enforced in CI) and the tiers (every push, nightly, before a tag).
- `testing/COVERAGE.md`: the coverage register (`testing/coverage-register.json`): every behaviour, route, job, migration, integration and scenario, the tests that prove each, and the totals; CI fails when something is unmapped or a proof is gone.
- `background-services.md`: every background service, what it does on an error at start-up, in its loop and on a timeout, and how Deluno restarts itself.
- `repo-change-history.md`: commit-by-commit and subsystem-by-subsystem summary of what changed from the last shared baseline.
- `deluno-capability-map.md`: product capability target and current direction.
- `DELUNO_END_TO_END_WORKFLOW.md`: canonical final-state media workflow and
  evidence requirements.
- `DELUNO_SETUP_ORDER.md`: ordered setup journey and readiness model derived
  from that workflow.
- `deluno-frontend-backend-map.md`: user-facing IA and backend ownership map.
- `deluno-ui-api-contract.md`: implemented UI-facing API contract and active gaps.
- `external-integration-api.md`: external automation and refine-before-import surface.
- `deluno-weir-contract.md`: every call and report between Deluno and Weir, each reply and what Deluno does with it, with the recording and test that prove each row (#1239).
- `RELEASE_PREFERENCE_SPEC.md`: typed release-preference semantics, truth tables, comparison order, and invariants.
- `guide-packages.md`: backend-owned guide package contract, provenance, mapping safety, and update procedure.
- `metadata-broker-contract.md`: managed TMDb gateway contract, deployment endpoint, and privacy boundary.
- `packaging.md`: source-backed Docker and Windows packaging guide.
- `bundled-components-install.md`: Windows installer component picker (download clients + Weir) — where it lives, the verify-before-run manifest, and how the setup wizard auto-wires what it finds (#640).
- `DEPLOYMENT.md`: current deployment instructions for Docker, Compose, and Windows publish runs.
- `TROUBLESHOOTING.md`: runtime, path-mapping, and startup troubleshooting.
- `backup-restore-runbook.md`: packaged Windows backup, restore, rollback, and migration recovery procedures.
- `ci-standard.md`: the CI and release standard shared with Weir: the `ci-passed` check, job names, path filters, pinning, permissions, timeouts, the release (two publish jobs, one per thing published, behind the same checks) and Dependabot.
- `ga-release-checklist.md`: RC1/RC2/GA checklist, gates, and sign-off workflow for `1.x` promotion.
- `golden-path.md`: the golden-path run on a clean VM that gates every release tag (#1158), shared byte for byte with Weir. Since #1238 it is a scripted scenario suite (`scripts/golden-path/Invoke-ScenarioSuite.ps1`) that runs before every tag and records the status itself.
- `windows-rc-validation-matrix.md`: clean-machine install/upgrade/rollback execution matrix for RC validation.
- `release-evidence-template.md`: standard issue comment template for RC/GA evidence and go/no-go decisions.
- `release-notes-1.0.0-draft.md`: draft 1.0.0 release notes structure for final GA publication.
- `upgrade-guide-0x-to-1x.md`: user upgrade procedure from prerelease line to GA line.
- `MEDIA_AUTOMATION_TERMINOLOGY.md`: familiar media-automation terms and Deluno's simpler, media-first equivalents.
- `tracker-definitions.md`: the tracker definition format Deluno's native tracker engine reads (#606, #986): fields, the built-in Prowlarr site list and which of it loads, how to add a definition without a Deluno release, and the two Deluno-written sites kept over it.
- `security-allowed-hosts.md`: System &rarr; Security &rarr; Allowed Hosts (#939) -- what it does, the setup-guide question, the lock-out protection, and the config/env-var + tray recovery path.

## Execution Plans

- `exec-plans/active/`: checked-in plans for larger in-flight work.
- `exec-plans/active/E2E-live-test-rc28-2026-10-03.md`: the current live test plan: a fresh install of the released build, used the way a person would use it.
- `exec-plans/active/FIX-BATCH-rc30-live-test.md`: the fix batch for every issue the rc.30 live test found, by area, with the rules each fix is held to.
- `exec-plans/active/live-test-rc30-user-action-tests.md`: for every rc.30 live test issue, the browser test or golden path step that does what the person did.
- `exec-plans/completed/`: completed plan history.
- `exec-plans/templates/`: starting points for large feature work and post-merge cleanup.
- `exec-plans/tech-debt-tracker.md`: recurring cleanup queue.

Current note:

- `agent-first-realignment.md` is completed work and now belongs under `exec-plans/completed/`.
- Finished handovers and dated test-run ledgers are deleted once their findings are in the backlog; git history keeps them.

## Repository map

- `apps/web/`: the web app (React and TypeScript). `apps/windows-tray/`: the Windows tray app.
- `src/`: the .NET backend, one project per module (host, API, worker, platform, movies, series, media, jobs, integrations, security, companions, shared contracts and the rest).
- `tests/`: the backend test projects, one per module, plus shared test support. `tests/fixtures/recorded/` holds the recorded real replies of the download clients, Weir and Deluno's side of that talk (#1237; format in its README); `scripts/recorded-replies/` records them on the golden VM.
- `docs/`: product, architecture and API documents (this index); plans are in `docs/exec-plans/`.
- `scripts/`: the push gate, the guard checks, packaging and release scripts; `scripts/lab/` provisions and checks the real-client test rig.
- `services/`: the metadata gateway, a small separate service the metadata broker talks to.
- `sdk/`: the TypeScript client for Deluno's API.
- `integrations/`: ready-made setups for other tools (Home Assistant).
- `installer/`: the Windows installer's bundled-component list and icon.
- `screenshots/`: the pictures the README shows.
- `.github/` and `.githooks/`: CI workflows and the local pre-push hook.

## Source Of Truth Rules

- When code and docs disagree, update the docs in the same change or log the drift in `QUALITY_SCORE.md`.
- Core repo maps should describe what is implemented now, not only what was intended in an earlier planning pass.
- Broader subsystem docs are useful orientation material, but agents should trust this index and the linked core docs first.

## Validation

Run:

```powershell
npm.cmd run validate:agents
```

The validation script checks that the agent entry point exists, required docs exist, stale workspace paths are not reintroduced, and high-signal architecture guardrails remain visible.

For GA-candidate evidence collection, run:

```powershell
npm run ga:regression
```

This writes step logs and a summary into `artifacts/ga-validation/<timestamp>-<candidate>/`.
