# Deluno 1.x GA Release Checklist

Updated: 2026-09-29

This checklist is the source of truth for promoting Deluno from `0.x` prerelease to `1.x` GA.

Parent tracking issue: #78

Execution artifacts:

- `docs/windows-rc-validation-matrix.md`
- `docs/release-evidence-template.md`
- `scripts/run-ga-regression.ps1` (invokable via `npm run ga:regression`)
- [The live test from a fresh install](exec-plans/active/E2E-live-test-rc28-2026-10-03.md): real indexers, real clients and real downloads, with every finding filed as an issue. On 24 Sep 2026 it and #639 (automatic upgrade-hunting proven live, once per media type) replaced the 14-day soak (#82), and the soak tools were removed on 3 Oct 2026.

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
- `RELEASE_NOTES.md` has a `## <version>` section for the version being
  released, written in plain everyday words (not internal names). The repo
  stays private ([#870](https://github.com/jampat000/Deluno/issues/870)), so
  this file, embedded into the build, is the only place an install can read
  what changed; the Updates card shows it for every version applied.
  The early-days notice opens every section; the GitHub release body takes the
  same wording from `.github/release-notice.md`, so change it there.
  `npm run ci:check` warns (does not fail) when the version currently in
  `Directory.Build.props` has no section yet — treat that warning as a real
  step for a release build, not one to wave through with the rest.
- Tags are made only through `scripts/tag-release.ps1 -Version <version>`, which refuses unless the tree is clean, HEAD is `origin/main`, `Directory.Build.props` and `RELEASE_NOTES.md` carry the version, CI passed on HEAD and ran the jobs, and the golden path passed on HEAD; only then does it create and push the annotated tag.
- CI is green on the exact commit that will be tagged: `ci.yml` has a push (or manual) run for that commit whose `ci-passed` job passed. **Tag only after that.** The release workflow's first job (`ci-passed`, `scripts/verify-ci-for-release.mjs`) checks it and fails at once if not. If the commit's own push run only skipped jobs because the change was documents, run `ci.yml` by hand on the commit first (`gh workflow run ci.yml --ref <commit>`).
- The single `release.yml` run succeeded end to end: `windows` and `docker` built, `publish` pushed the image, created the GitHub Release with its assets and `SHA256SUMS.txt`, and moved the moving image tags last. Open the run, the GitHub Release and `docker pull ghcr.io/jampat000/deluno:<version>` to see it; do not report a release done on the tag alone.
- The golden path (`docs/golden-path.md`) has passed on the exact commit that will be tagged, on a clean VM: run `scripts/golden-path/Invoke-ScenarioSuite.ps1` on that commit's installer, which records the `golden-path` status itself. Tag only after that; there is no waiver, and the release's `golden-path` job checks the record and does not wait. The suite proves these, unattended, with evidence (the lines it does not script yet are listed in `docs/golden-path.md`):
  - The installer runs on the clean VM and offers the download clients and Weir.
  - Setup asks each automatic choice as Yes or No, and Connect Weir works.
  - One torrent film, one Usenet film and one TV episode download, are cleaned and imported with a poster, and Activity tells each story in plain words.
  - A stalled or failed download is replaced or offered **Try again**, and a removed title comes back from the recycle bin.
  - Every screen is clicked through (next item).
- Every screen has been clicked through on the candidate build: every screen in the menu, at a wide desktop width, a laptop width and phone width. Nothing is broken, empty by mistake or showing an internal code, and every counted problem can be cleared on the screen. Do this before each tag and after each batch of fixes. Record it as the "5 Every screen" row of the live-test table in the release evidence (`docs/release-evidence-template.md`), with the build tested and the issue number of anything found.
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

## How a release is built

`release.yml` runs on a `v*` tag and is the only workflow that publishes (the separate Docker workflow is gone). Its jobs:

| Job | What it does |
|---|---|
| `ci-passed` | Fails unless `ci.yml` already passed on the tagged commit. Does not wait. |
| `windows` | Packs the Velopack installer, portable zip and update packages, bundles pinned FFmpeg and UnRAR (SHA-256 checked), signs when a certificate exists, writes `SHA256SUMS.txt`. |
| `docker` | Builds the amd64 image unpushed, scans it with Trivy (fails on a fixable CRITICAL) and starts it. |
| `publish` | Needs the three above. Pushes the image with provenance and an SBOM, starts the pushed digest, creates the GitHub Release (prerelease and the `rc` channel for `0.x` and `-rc` tags), then moves `major.minor` and `latest`. |

`ci-passed` in `ci.yml` is the one check branch protection requires. The standard is `docs/ci-standard.md`.

Signed build-provenance attestations are off until the repository is public (set the repository variable `ATTEST_PROVENANCE` to `true` then); they are not available on a private repository outside GitHub Enterprise Cloud.

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
- [ ] The live test from a fresh install passes, and automatic upgrade-hunting is proven live once per media type ([#639](https://github.com/jampat000/Deluno/issues/639)). Both replaced the 14-day soak ([#82](https://github.com/jampat000/Deluno/issues/82), closed 24 Sep 2026).
- [x] Backup/restore drill succeeds on a second machine profile ([#83](https://github.com/jampat000/Deluno/issues/83)). — Rollback C on the lab VM; the first attempt failed and the recovery path was repaired (#403).
- [ ] RC2 release notes draft exists and matches shipped behavior. — the draft exists (`release-notes-1.0.0-draft.md`, last touched 2026-09-02); **whether it still matches is unverified**, and behaviour has moved since.

Exit criteria:

- No unresolved critical regressions from RC1 scope.
- The live test has no unresolved `P0`/`P1` defects.

## GA Checklist (`1.0.0`)

- [ ] The live test's summary is posted and approved, with every finding filed and resolved.
- [x] Backup/restore runbook is published and linked ([#83](https://github.com/jampat000/Deluno/issues/83)). — `backup-restore-runbook.md`, linked from `docs/README.md` and the root README.
- [x] Regression evidence is posted for GA candidate commit ([#85](https://github.com/jampat000/Deluno/issues/85)). — commit `b73a8ae`: 1,598 backend tests and 304 browser checks, posted on [#81](https://github.com/jampat000/Deluno/issues/81).
- [x] User-facing upgrade notes are published ([#86](https://github.com/jampat000/Deluno/issues/86)). — `upgrade-guide-0x-to-1x.md`, linked from `docs/README.md` and the root README.
- [ ] Final release decision log is filled and attached (template below). — nothing to decide on until the live test reports.
- [ ] Final sign-off is recorded in issue [#78](https://github.com/jampat000/Deluno/issues/78) and child issues are closed.

Exit criteria:

- All hard gates pass.
- All `#78` child items are closed with evidence.
- `1.0.0` tag is created from the approved candidate commit.

## Required Sign-Off Order

1. Build and release owner confirms workflow and artifact integrity.
2. QA owner confirms matrix + regression gates.
3. Product owner confirms user-facing notes and upgrade guidance.
4. Final approver confirms GA decision in issue #78.

## Release Decision Log

### rc.22: NO-GO (29 Sep 2026, James)

Candidate tag: `v1.0.0-rc.22`. Decision: **NO-GO**, not released. Reason
(James, on [#941](https://github.com/jampat000/Deluno/issues/941)): rc.22 is
superseded by about 60 fixes on `main`. The candidate is cut again as
`1.0.0-rc.23` once the installer's all-six clean-VM run passes
([#968](https://github.com/jampat000/Deluno/issues/968)) and the rig checks
pass ([#959](https://github.com/jampat000/Deluno/issues/959),
[#1027](https://github.com/jampat000/Deluno/issues/1027)); the GA regression
is then run on rc.23 and its summary and decision log are posted on #941.

### rc.23: pending

Candidate: `1.0.0-rc.23` (`Directory.Build.props` and `RELEASE_NOTES.md`
already carry it). No decision yet. The regression summary draft is
`artifacts/rc23-regression-summary.md`; the final CI, clean-VM and
Release/Docker run links go there before a GO or NO-GO is posted. The
"Final release decision log" box above stays unticked until that decision.

### rc.24: pending

Candidate: `1.0.0-rc.24`, cut on 30 Sep 2026 from rc.23 plus
[#1081](https://github.com/jampat000/Deluno/issues/1081) to
[#1086](https://github.com/jampat000/Deluno/issues/1086) (instant screens, the
tabbed Dashboard, database trimming, consistent backups, plain words). No GO or
NO-GO has been posted for rc.23 or rc.24.

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
- Live test from a fresh install:
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
