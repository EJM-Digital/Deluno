# Windows RC Validation Matrix

Updated: 2026-08-21

This matrix is the execution guide for issue #81.
Use it for RC1 and RC2 only after a reproducible candidate artifact is available.

Unsigned local candidates may be used for installer-mechanics validation on a
disposable test VM. A signed, published release candidate is still required
before this matrix can be used as GA evidence.

## Target Candidate

- Candidate tag: `0.2.1-rc` (local validation candidate; not published)
- Candidate commit: current `main` worktree, starting at `1846012` plus the
  pending release-readiness work
- Setup asset: `artifacts/vm-validation/channel-proof-20260815/velopack/Deluno-rc-Setup.exe`
- SHA-256: `1BA8A4E5D0A8D6A7EEF379D1A14D8D833467ABC58337795506D2EAF8564C990F`
- Release URL: not applicable — deliberately not published
- Tester: Codex / product owner
- Test date: 2026-08-15

### Current Environment

- Clean A VM: `Deluno Test Server 2025`
- Guest OS: Windows Server 2025 Standard Evaluation (Desktop Experience)
- VM: Generation 1, 4 vCPU, 12 GB RAM, Default Switch
- Snapshot: pre-install checkpoint captured

## Environment Profiles

Run each scenario on a clean Windows profile or VM snapshot.

Profile definitions:

- `Clean A`: no prior Deluno install
- `Upgrade B`: existing `v0.1.x` packaged install with realistic data
- `Rollback C`: same as Upgrade B, with forced update-failure simulation
- `Clean D`: clean Windows 11 24H2 or later VM with Smart App Control in
  enforcement mode (`VerifiedAndReputablePolicyState = 1`)

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
   - channel matches the candidate feed (`rc` for an RC, `stable` for GA)
5. Create a manual backup in `System > Backups`.
6. Run one search + one import smoke flow.

Pass criteria:

- app launches successfully
- updates screen reports packaged mode
- backup creation succeeds
- smoke flows complete without critical error

### Recorded result — 2026-08-15

| Check | Result | Evidence |
| --- | --- | --- |
| Clean install of local `0.2.0-rc` candidate | PASS (installer/runtime mechanics) | Windows Server 2025 VM, packaged install, readiness endpoint returned healthy |
| Manual backup | PASS | One backup created at `2026-08-15T00:49:23Z` |
| In-place update to local `0.2.1-rc` candidate | PASS | Existing account, backup, and update preferences remained available |
| RC channel isolation | PASS after fix | `0.2.1` reported `channel: rc`, `updateAvailable: false`, and did not queue public stable `v1.1.2` |

The original `0.2.0-rc` build exposed a release-channel defect: fresh installs
defaulted to `stable` and downloaded the public stable update. The fix stamps
the build channel into the tray assembly, sends 0.x/prerelease tags to the RC
feed, and lets RC clients query prerelease releases. This local unsigned result
validates mechanics only; a signed, published candidate still needs the full
matrix before issue #81 can close.

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

### Recorded result — 2026-08-15

| Check | Result | Evidence |
| --- | --- | --- |
| Published `v0.1.5` install | PASS | Product version reported `0.1.5+b4aeedc...` on the clean Windows Server 2025 VM |
| Installer handoff to local `0.2.1-rc` | PASS | Product version reported `0.2.1-rc.1+1846012...` after the RC installer completed |
| RC first run | PASS | All database, writable-storage, worker-heartbeat, and queue readiness checks passed |
| Update channel | PASS | `System > Updates` API reported `channel: rc`, `windows-packaged`, and no stable update queued |
| In-app RC update | PASS | Local `0.2.1 -> 0.2.2` RC feed check, download, pre-update backup, restart, and readiness check all succeeded |

The published `v0.1.5` build launched as a tray-only process on this Server
2025 target and exposed no local API or persisted library configuration to
seed. This proves the genuine released-binary installer migration and new
runtime health, but it does **not** replace the separate `0.2.0 -> 0.2.1`
data-preservation check already recorded above. A signed, published RC and a
realistic populated 0.1.x profile are still required for the full closure
criteria.

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

### Recorded result — 2026-08-15

| Check | Result | Evidence |
| --- | --- | --- |
| Controlled failed apply | PASS | A valid `0.2.3-rc` update was downloaded, then only the staged VM package was truncated before restart |
| Recovery version | PASS | Deluno restarted on the last known-good `0.2.2-rc.1+1846012...` binary |
| Runtime health after failure | PASS | Readiness endpoint returned healthy: platform/movies/series/jobs/cache databases, writable storage, worker heartbeat, and queue |
| Data protection | PASS | A `pre-update` backup was created before the failed apply; prior backup remained available |
| Failure evidence | PASS | `velopack.log` recorded the corrupt package error (`End of Central Directory record could not be found`) without leaving Deluno unusable |

This was an intentionally corrupted **staged package** in a disposable VM,
with a checkpoint captured before the simulation. It validates the actual
Velopack apply/restart recovery path; no production release asset was changed.

## Scenario 4: Smart App Control Enforcement (Clean D)

Goal:

- verify that the signed published candidate can install and launch on a clean
  Windows 11 machine where Smart App Control enforcement is enabled

Precondition:

- a signed, published candidate is available; unsigned local builds are not
  valid evidence for this scenario
- before installation, confirm that
  `(Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy").VerifiedAndReputablePolicyState`
  returns `1`

Steps:

1. Capture the SAC state and Windows version before installation.
2. Verify the downloaded setup executable's SHA-256 against
   `SHA256SUMS.txt` and confirm `Get-AuthenticodeSignature` reports `Valid`.
3. Run the signed `*Setup*.exe` on the clean VM.
4. Record whether installation completes and Deluno launches, or SAC blocks
   either action.
5. If blocked, capture the Windows block dialog and the signer details; do not
   disable SAC or bypass the warning on the evidence VM.

Pass criteria:

- the signed setup executable installs successfully
- Deluno launches after installation and the first health check is usable
- the update screen reports the expected packaged install kind and candidate
  channel

### Recorded result — pending signed candidate

| Check | Result | Evidence |
| --- | --- | --- |
| SAC-enforcing clean Windows 11 install and first launch | NOT RUN | Blocked until a signed published candidate and Clean D VM are available |

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
| SAC enforcement | Clean D | NOT RUN | pending signed candidate and VM | |
```
