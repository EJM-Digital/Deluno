# Windows RC Validation Matrix

Updated: 2026-05-14

This matrix is the execution guide for issue #81.
Use it for RC1 and RC2 only after signed release artifacts are available.

## Target Candidate

- Candidate tag:
- Candidate commit:
- Setup asset:
- Release URL:
- Tester:
- Test date:

## Environment Profiles

Run each scenario on a clean Windows profile or VM snapshot.

Profile definitions:

- `Clean A`: no prior Deluno install
- `Upgrade B`: existing `v0.1.x` packaged install with realistic data
- `Rollback C`: same as Upgrade B, with forced update-failure simulation

Record for each run:

- Windows version
- VM or hardware profile
- Install path
- Data root path

## Scenario 1: Fresh Install (Clean A)

Goal:

- verify first-time install and first launch behavior

Steps:

1. Download `*Setup*.exe` from candidate release.
2. Install with default options.
3. Launch Deluno and wait for UI availability.
4. Open `System > Updates` and confirm:
   - install kind is `windows-packaged`
   - channel is `stable`
5. Create a manual backup in `System > Backups`.
6. Run one search + one import smoke flow.

Pass criteria:

- app launches successfully
- updates screen reports packaged mode
- backup creation succeeds
- smoke flows complete without critical error

## Scenario 2: Upgrade From Latest 0.1.x (Upgrade B)

Goal:

- verify seamless user upgrade from prerelease line

Precondition:

- machine has latest `0.1.x` installed and working

Steps:

1. Confirm baseline version and health on `0.1.x`.
2. Trigger update check/download from `System > Updates`.
3. Apply update via restart flow.
4. Relaunch and confirm new RC version.
5. Validate:
   - settings retained
   - libraries/indexers/download clients retained
   - queue and import screens load
   - one end-to-end search/grab/import run

Pass criteria:

- version advances to candidate
- no loss of core settings/data
- no critical post-upgrade workflow regressions

## Scenario 3: Failed Update and Rollback (Rollback C)

Goal:

- prove recovery path when update apply fails

Recommended simulation options (choose one and record):

- temporarily break candidate payload on test channel
- block update payload read at apply time
- inject known failure point in controlled test build

Steps:

1. Start from healthy `0.1.x` or RC baseline.
2. Trigger update download.
3. Force apply failure via chosen simulation method.
4. Observe updater outcome and restart behavior.
5. Verify app returns to last known-good version.
6. Validate core app health and data integrity.

Pass criteria:

- failed apply does not leave app unusable
- prior working version remains operable
- core data remains intact

## Evidence Capture Requirements

Capture for each scenario:

- screenshot: update status before action
- screenshot: update status after action
- screenshot: version and install kind after restart
- screenshot or log: backup creation success
- short log excerpt for any failure

Attach in issue #81:

- scenario-by-scenario pass/fail table
- links to screenshots/logs
- final recommendation (`GO RC2` or `NO-GO`)

## Pass/Fail Summary Table

Use this block in issue #81:

```md
| Scenario | Environment | Result | Evidence | Notes |
| --- | --- | --- | --- | --- |
| Fresh install | Clean A | PASS/FAIL | <link> | |
| Upgrade 0.1.x -> RC | Upgrade B | PASS/FAIL | <link> | |
| Failed apply rollback | Rollback C | PASS/FAIL | <link> | |
```
