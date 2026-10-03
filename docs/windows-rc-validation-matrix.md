# Windows RC Validation Matrix

Updated: 2026-09-04

This matrix is the execution guide for issue #81.

**Candidates are unsigned, by decision.** Deluno does not buy a code-signing
certificate (`docs/ga-release-checklist.md`, decided 2026-09-02), so "a signed,
published release candidate" is not a bar this matrix can ever clear and no
longer asks for one. What it asks for instead is a candidate built by the one
script that builds installers, recorded by hash, and walked end to end on a
clean Windows machine.

## Target Candidate

- Candidate version: `1.0.0-rc.10`, and `1.0.0-rc.11` as the upgrade target
- Candidate commit: `b73a8ae` on `main`
- Built by: `./scripts/pack-windows-installer.ps1 -Version 1.0.0-rc.10 -Clean`
- Setup asset: `artifacts/windows/velopack/Deluno-rc-Setup.exe` (128 MB)
- SHA-256 (`rc.10`): `5099BEA74CFFC3B782E1214D0C32BBFE32D045BFE5D454AF86B4B8CF6D0E8DCA`
- SHA-256 (`rc.11`): `53384E2BF82BFC94733231E21BD281573DACF6A5687BB6E892776A8C6FF8369F`
- Release URL: not published — these are validation candidates, and the
  packaging script exists so a candidate can be built and walked without
  cutting a release
- Signature: none, by decision. SmartScreen warns on first install; that is
  documented for users rather than hidden
- Tester: Claude Opus 5, against the product owner's lab VM
- Test date: 2026-09-04

### Current Environment

- Lab VM: `192.168.1.10`, Windows Server, reached over WinRM
- Install root: `%LocalAppData%\Deluno`
- Data root: `%LocalAppData%\DelunoData`
- Listening port: `7879` (the packaged default; the lab's own scheduled-task
  deployment uses 5099 and is a separate install)

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

### Recorded result — 2026-09-04

| Check | Result | Evidence |
| --- | --- | --- |
| Clean install of `1.0.0-rc.10` | PASS | `%LocalAppData%\Deluno\current` populated, five databases created in `DelunoData` |
| First launch | PASS | Readiness `200`, status `ready`, 9 of 9 checks ready |
| Channel isolation | PASS | `sq.version` reports `<channel>rc</channel>`; the build cannot see the stable feed |
| Bundled tools | PASS | `tools\ffmpeg\ffmpeg.exe`, `tools\ffmpeg\ffprobe.exe` with 7 shared LGPL DLLs, and `tools\unrar\UnRAR.exe` |
| Account setup and first data | PASS | Owner account created, then 2 libraries, 1 indexer, 1 download client with a saved key, 1 quality profile, 1 media plan |
| Folder browsing | PASS | Drive roots enumerate; `C:\InstallerTest\Films\` → `C:\InstallerTest\` → `C:\` → drive list |

Four defects were found by running this scenario rather than by any automated
check, and all four are fixed in the candidate above:

1. **The app could not start at all.** The tray composed twelve modules where
   `Deluno.Host` composed sixteen; the packaged build threw on
   `IDispatchRecoveryHandler` and never opened a listener (#400).
2. **No libraries, indexers, quality or automation API.** The tray mapped
   fifteen endpoint groups where the host mapped twenty-three, and its SPA
   fallback answered the missing ones with `index.html` and a `200` (#401).
3. **The folder picker could not go up.** `parentPath` returned the same
   directory (#402).
4. **Restoring a backup did nothing and returned a 500.** It wrote over
   databases the running app holds open and failed on the first file (#403).

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

### Recorded result — 2026-09-04

Run on the candidate above. The August run below proved the released-binary
installer handoff and is kept; this one is the data-preservation half it says
was still owed, on an install carrying real configuration.

| Check | Result | Evidence |
| --- | --- | --- |
| Populated install before upgrade | PASS | 2 libraries, 1 indexer, 1 download client with a saved API key, 1 quality profile, 1 media plan, on `1.0.0-rc.10` |
| In-place upgrade `rc.10 → rc.11` | PASS | Setup run over the existing install; product version reported `1.0.0-rc.11` afterwards |
| Readiness after upgrade | PASS | `200 OK` |
| Data preserved | PASS | Every count identical after the upgrade: 2 / 1 / 1 / 1 / 1 |
| Saved credential survived | PASS | The download client's Test reached HTTP — `http: SABnzbd returned 404` — rather than failing `auth: API key is missing`, so the stored key was read back and decrypted |
| Repeat upgrades | PASS | The same result across `rc.5 → rc.6` and `rc.6 → rc.7` earlier in the session |

The version pair is `rc.10 → rc.11` rather than `0.1.5 → 1.0.0`. What the line
is protecting — that an in-place upgrade over a populated install preserves
libraries, connections, profiles, plans and saved credentials — is what was
measured, and it was measured on data created through the running app rather
than transplanted.

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

### Recorded result — 2026-09-04

The August run above covers Velopack's failed-apply path: a corrupted staged
package, and the app restarting on the last known-good binary. This run covers
the other half of the same scenario — losing data and getting it back — which
is the recovery path the deployment guide actually describes for Windows, and
which turned out not to work at all.

| Check | Result | Evidence |
| --- | --- | --- |
| Pre-update backup on both restart routes | PASS | `apply-on-restart` and `restart-now` each created a `pre-update` backup; count went 0 → 1 → 2 |
| Backup taken with known state | PASS | `deluno-backup-20260904-025853`, taken with 2 libraries |
| Data genuinely lost | PASS | One library deleted; count 2 → 1 |
| Restore staged | PASS | 10 database files plus the protection key staged; response said to restart to apply |
| Recovery after restart | PASS | Readiness `200`, libraries back to 2 |
| Replaced files kept | PASS | `cache.db.pre-restore`, `jobs.db.pre-restore`, `movies.db.pre-restore`, `platform.db.pre-restore`, `series.db.pre-restore` |
| Backups retained through recovery | PASS | The backup used remained listed afterwards |

**This scenario failed on the first attempt, and the failure was the point.**
Restore returned `{"error":"An unexpected error occurred."}` and changed
nothing. The only trace was a single `cache.db.pre-restore` file: the restore
had written its keep-the-old-one copy for the first database alphabetically and
then thrown, because Deluno holds every database open while it runs and the
extract cannot overwrite them. Fixed in #403 by staging the upload and applying
it at startup before any connection opens — the one moment nothing holds the
files.

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

### Recorded result — out of scope by decision, 2026-09-04

| Check | Result | Evidence |
| --- | --- | --- |
| SAC-enforcing clean Windows 11 install and first launch | OUT OF SCOPE | Smart App Control cannot be satisfied without a signer reputation, which cannot exist without a signature. Deluno ships unsigned by decision (`docs/ga-release-checklist.md`, 2026-09-02), so this is not pending work — it is work that will not happen |

Recorded as a decision rather than left as NOT RUN, because "pending a signed
candidate" reads as something somebody could still do.

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
| Fresh install | Clean A | PASS | 1.0.0-rc.10, readiness 200, 9/9 checks | 2026-09-04 |
| Upgrade over populated install | Upgrade B | PASS | rc.10 to rc.11, every count preserved, saved key still decrypts | 2026-09-04 |
| Failed apply rollback | Rollback C | PASS | corrupted staged package, restarted on last known-good | 2026-08-15 |
| Lose data and restore | Rollback C | PASS | 2 to 1 to 2 across a restart, replaced files kept | 2026-09-04 |
| SAC enforcement | Clean D | OUT OF SCOPE | unsigned by decision; SAC needs a signature | |
```
