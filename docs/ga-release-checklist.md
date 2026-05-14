# Deluno 1.x GA Release Checklist

Updated: 2026-05-14

This checklist is the source of truth for promoting Deluno from `0.x` prerelease to `1.x` GA.

Parent tracking issue: #78

Execution artifacts:

- `docs/windows-rc-validation-matrix.md`
- `docs/release-evidence-template.md`
- `scripts/run-ga-regression.ps1` (invokable via `npm run ga:regression`)

## Promotion Stages

1. `RC1`: first signed candidate on real release pipeline
2. `RC2`: hardening candidate after RC1 feedback
3. `GA`: final `1.0.0` promotion

## Hard Gates (Must Pass)

- Windows signing certificate is configured and valid in CI.
- `1.x` release workflow fails when signing secrets are missing.
- Windows setup and app binaries are signed and signature-valid.
- Install/upgrade/rollback matrix passes on clean Windows environments.
- No open `P0` or `P1` release-blocking issues.
- Full regression gates pass on candidate commit:
  - `npm run ci:check`
  - `dotnet test Deluno.slnx --configuration Release`
  - `npm run test:web`

## RC1 Checklist

- [ ] Signing cert provisioning issue is complete (#79).
- [ ] 1.x signed release gate enforcement is complete (#80).
- [ ] RC1 tag is cut with signed artifacts and published.
- [ ] Clean-machine fresh install test passes.
- [ ] Upgrade test from latest `0.1.x` to RC1 passes.
- [ ] Rollback simulation result is documented.
- [ ] RC1 validation summary is posted to issue #78.

Exit criteria:

- No critical installer/updater defects discovered in RC1.
- If defects exist, they are fixed and re-verified before RC2.

## RC2 Checklist

- [ ] RC1 defects are fixed and linked in issue #78.
- [ ] Installer/upgrade/rollback matrix is rerun and passes (#81).
- [ ] 14-day soak starts with daily checks recorded (#82).
- [ ] Backup/restore drill succeeds on a second machine profile (#83).
- [ ] RC2 release notes draft exists and matches shipped behavior.

Exit criteria:

- No unresolved critical regressions from RC1 scope.
- Soak has no unresolved `P0`/`P1` defects.

## GA Checklist (`1.0.0`)

- [ ] Soak completion summary is posted and approved (#82).
- [ ] Backup/restore runbook is published and linked (#83).
- [ ] Regression evidence is posted for GA candidate commit (#85).
- [ ] User-facing upgrade notes are published (#86).
- [ ] Final release decision log is filled and attached (template below).
- [ ] Final sign-off is recorded in issue #78 and child issues are closed.

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
