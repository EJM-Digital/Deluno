# Deluno 1.x GA Release Checklist

Updated: 2026-09-05

This checklist is the source of truth for promoting Deluno from `0.x` prerelease to `1.x` GA.

Parent tracking issue: #78

Execution artifacts:

- `docs/windows-rc-validation-matrix.md`
- `docs/release-evidence-template.md`
- `scripts/run-ga-regression.ps1` (invokable via `npm run ga:regression`)
- `docs/soak-plan.md` and `scripts/collect-soak-snapshot.ps1` (invokable via `npm run soak:snapshot`)

## Promotion Stages

1. `RC1`: first candidate on real release pipeline
2. `RC2`: hardening candidate after RC1 feedback
3. `GA`: final `1.0.0` promotion

## Code signing: Deluno ships unsigned

**Decided 2026-09-02.** Deluno does not buy a code-signing certificate, and
releases are published unsigned.

The consequence is real and is not hidden from users: **Windows SmartScreen
warns on first install**, and the person has to choose "More info" then "Run
anyway". Say so in the release notes and the install guidance every time,
rather than letting people meet it unprepared and assume the download is
broken.

This is a reversal. `release.yml` used to hard-fail any 1.x build without a
certificate, which meant that once the decision was made no 1.x release could
be produced at all. The workflow now reports signing status as a warning and
signs only if the secrets are present, so adding a certificate later needs no
code change - just `WINDOWS_SIGN_CERT_BASE64` and `WINDOWS_SIGN_CERT_PASSWORD`.

Smart App Control is a separate matter and stays out of scope for the same
reason: it cannot be satisfied without a signer reputation, which cannot exist
without a signature.

## Hard Gates (Must Pass)

- Windows release artifacts are complete and installable.
- Install/upgrade/rollback matrix passes on clean Windows environments.
- No open `P0` or `P1` release-blocking issues.
- Full regression gates pass on candidate commit:
  - `npm run ci:check`
  - `dotnet test Deluno.slnx --configuration Release`
  - `npm run test:web`

  Run these one at a time. A second `dotnet` process against the same solution
  takes file locks on the built assemblies and fails the first with
  `MSB3027`/`MSB3021`, which reads like a broken build and is not one.

  **A green run has to be a run you watched finish.** The intermittent test-host
  hang ([#333](https://github.com/jampat000/Deluno/issues/333)) is back and now
  reproduces under the serial runner and on CI: on 6 September the backend job
  sat silent on `Deluno.Persistence.Tests` for nineteen minutes and was
  cancelled, and the same commit passed on a plain re-run. A cancelled gate is
  not a failed gate, and neither is evidence.

## RC1 Checklist

- [ ] RC1 tag is cut and published with required artifacts. — **not done deliberately.** The validated candidate (`rc.10`/`rc.11`, commit `b73a8ae`) was built with `scripts/pack-windows-installer.ps1` and walked on the lab VM without cutting a release, which is what that script exists for. Published `1.x` releases exist but are not the candidate that was validated.
- [x] Clean-machine fresh install test passes. — `1.0.0-rc.10` on a clean profile, readiness 200 with 9/9 checks ([#81](https://github.com/jampat000/Deluno/issues/81)).
- [x] Upgrade test from latest `0.1.x` to RC1 passes. — **run as `rc.10 → rc.11`** over a populated install, on the owner's instruction; libraries, connections, profiles, plans and the saved client key all survived ([#81](https://github.com/jampat000/Deluno/issues/81)).
- [x] Rollback simulation result is documented. — 2 → 1 → 2 libraries across a restart, five `.pre-restore` copies kept ([#81](https://github.com/jampat000/Deluno/issues/81), `windows-rc-validation-matrix.md`).
- [x] RC1 validation summary is posted to issue [#78](https://github.com/jampat000/Deluno/issues/78).

Exit criteria:

- No critical installer/updater defects discovered in RC1.
- If defects exist, they are fixed and re-verified before RC2.

## RC2 Checklist

- [x] RC1 defects are fixed and linked in issue [#78](https://github.com/jampat000/Deluno/issues/78). — four found by running the installer, all fixed and re-verified (#400–#403), plus packaging (#404).
- [x] Installer/upgrade/rollback matrix is rerun and passes ([#81](https://github.com/jampat000/Deluno/issues/81)). — ten of twelve lines met; the two outstanding both want a signed installer, which is the decision above.
- [ ] 14-day soak starts with daily checks recorded ([#82](https://github.com/jampat000/Deluno/issues/82)); follow [the soak plan](soak-plan.md). — **not started, and the collector was not ready either.** `npm run soak:snapshot` had never once taken a reading on Windows PowerShell 5.1: it recorded every endpoint as down and exited 0 ([#461](https://github.com/jampat000/Deluno/issues/461)). It works now, decides six of the seven daily checks itself, and can schedule its own run. What remains is wall-clock time on a machine holding a real library — and the plan's own restart rule means the clock should not start until the end-to-end pass has settled the build.
- [x] Backup/restore drill succeeds on a second machine profile ([#83](https://github.com/jampat000/Deluno/issues/83)). — Rollback C on the lab VM; the first attempt failed and the recovery path was repaired (#403).
- [ ] RC2 release notes draft exists and matches shipped behavior. — the draft exists (`release-notes-1.0.0-draft.md`, last touched 2026-09-02); **whether it still matches is unverified**, and behaviour has moved since.

Exit criteria:

- No unresolved critical regressions from RC1 scope.
- Soak has no unresolved `P0`/`P1` defects.

## GA Checklist (`1.0.0`)

- [ ] Soak completion summary is posted and approved ([#82](https://github.com/jampat000/Deluno/issues/82)). — follows the soak.
- [x] Backup/restore runbook is published and linked ([#83](https://github.com/jampat000/Deluno/issues/83)). — `backup-restore-runbook.md`, linked from `docs/README.md` and the root README.
- [x] Regression evidence is posted for GA candidate commit ([#85](https://github.com/jampat000/Deluno/issues/85)). — commit `b73a8ae`: 1,598 backend tests and 304 browser checks, posted on [#81](https://github.com/jampat000/Deluno/issues/81).
- [x] User-facing upgrade notes are published ([#86](https://github.com/jampat000/Deluno/issues/86)). — `upgrade-guide-0x-to-1x.md`, linked from `docs/README.md` and the root README.
- [ ] Final release decision log is filled and attached (template below). — nothing to decide on until the soak reports.
- [ ] Final sign-off is recorded in issue [#78](https://github.com/jampat000/Deluno/issues/78) and child issues are closed. — [#82](https://github.com/jampat000/Deluno/issues/82) is the only child still open.

Exit criteria:

- All hard gates pass.
- All `#78` child items are closed with evidence.
- `1.0.0` tag is created from the approved candidate commit.

## Required Sign-Off Order

1. Build and release owner confirms workflow and artifact integrity.
2. QA owner confirms matrix + regression gates.
3. Product owner confirms user-facing notes and upgrade guidance.
4. Final approver confirms GA decision in issue #78.

## Release Decision Log Template

Use this block in issue #78 for RC1, RC2, and GA decisions.

```md
## Release Decision: <RC1|RC2|GA>
Date:
Candidate tag/commit:
Decision: <GO | NO-GO>

Gate summary:
- Signing:
  Note whether binaries were signed for this candidate.
- Install/upgrade/rollback matrix:
- Soak:
- Regression suite:
- Backup/restore:
- User docs:

Open risks:
- 

Approvals:
- Build/Release:
- QA:
- Product:
- Final approver:
```
