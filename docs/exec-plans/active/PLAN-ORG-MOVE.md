# Plan: moving Deluno to the EJM-Digital organisation

**Status, 20 Oct 2026:** the flip commit (steps 1 to 5, 7 and 8 below, the image copy workflow and the rc.30.35 release notes) is
prepared on a branch and lands right after the transfer. Step 6 (git remotes) is done by hand after the transfer.

Deluno moves from `jampat000/Deluno` to `EJM-Digital/Deluno` when the owner clicks Transfer. Weir has already moved to
`EJM-Digital/Weir` (image `ghcr.io/ejm-digital/weir` from 1.0.0-rc.19). The work before the transfer makes the name one value
per language, so the flip after it is small and checked.

## What is already done (before the transfer)

- **One value per language.** `repository.json` (scripts, installer tooling) and `src/Deluno.Contracts/ProjectRepositories.cs`
  (C#, including the tray, which already references Deluno.Contracts). Web code carries no repository address: issue links in
  code comments are a bare `#NNN`.
- **Weir is flipped now**: `ProjectRepositories.Weir`, `installer/bundled-components.json`, the Weir feed read in
  `scripts/golden-path/Invoke-ScenarioSuite.ps1` (from `repository.json`) and `tests/e2e-windows/scenarios/W02-GithubPicker.ps1`.
- **Guard.** `node scripts/check-repository-references.mjs` (in `npm run ci:check`) fails when the old owner name appears in
  `src/`, `apps/`, `installer/`, `scripts/`, `.github/`, `Dockerfile*`, `docker-compose*`, `*.props` or a root `*.json`, outside the
  declared single-value files. Files that point at Deluno's current repository on purpose are listed in `PENDING_FLIP` in that
  script (today: `.github/ISSUE_TEMPLATE/config.yml`). `docs/`, `*.md` and `tests/` are out of scope.
- **Installed trays keep finding updates.** The tray used to save `UpdateSource` (the old address) in its settings file. A saved
  value equal to a former built-in default (`ProjectRepositories.FormerDelunoWebUrls`) is now read as "use the built-in default",
  and the file is written with the update address only when it is a real override. After the flip, an installed copy that updates
  to the first release carrying the flip reads the new address by itself. Until a copy has run that release it still looks at the
  old address, which GitHub redirects to the new one after a transfer (releases, atom feed and downloads follow the redirect).
- **The Docker update notice** needs no change. Deluno has no in-app Docker update checker (`UpdateAvailable` is always false and
  `LatestVersion` null: `src/Deluno.Api/Updates/DefaultUpdateOrchestrator.cs:346-364`). The page shows the running image from
  `DELUNO_IMAGE_REF` (`apps/web/src/routes/system-screen.tsx:1644`). The image a release names is written into that release's body
  by the release workflow from `github.repository` at publish time (`.github/workflows/release.yml:534-536` and `:592`), so a
  container on rc.30.34 reads the new name from the new release, never from a constant in the running build.
- **Images of old releases are copied, then the old package is deleted** (the owner, 20 Oct 2026: nothing of Deluno or Weir stays
  under the personal account). ghcr does not forward a moved name, so after the transfer the one-off workflow
  `.github/workflows/copy-former-images.yml` (manual run; GitHub-hosted runner; `packages: write` on its one job) lists every tag of
  the old package (name in `repository.json`, `formerDelunoImage`), copies each to `ghcr.io/ejm-digital/deluno:<tag>` with `crane copy`
  (attestation and signature `sha256-*` tags included) and fails unless every tag has the same digest under both names. From then on
  there is one image name for every version: `ProjectRepositories.DelunoImage`. The old per-version helper
  (`DelunoImageForVersion` / `delunoImageForVersion`) is gone. The old package must let the new repository read it (package settings,
  "Manage Actions access"), or be public, or the copy cannot list it.
- **Recorded GitHub replies** under `tests/fixtures/recorded/github/` were taken from the old Weir address and stay as recorded
  (real replies). The replay treats the owner of `/Weir` as not part of what is asked
  (`tests/fixtures/RecordedReplies.cs`), and the tests that follow download addresses inside a recorded reply expect the recorded
  owner. Re-record from `EJM-Digital/Weir` when convenient, then remove that normalisation.

## The flip, after the transfer

Do these in one commit on `main` and push once:

1. `repository.json`: `deluno` to `EJM-Digital/Deluno`.
2. `src/Deluno.Contracts/ProjectRepositories.cs`: `Deluno` to `EJM-Digital/Deluno`. Keep `FormerDelunoWebUrls` (what old installs
   still carry). Check `ProjectRepositoriesTests` still pass.
3. `.github/ISSUE_TEMPLATE/config.yml` (security advisory and SECURITY.md links) and `.github/release-notice.md` (names the repository
   in the release body): point at `EJM-Digital/Deluno`; then empty `PENDING_FLIP` in `scripts/check-repository-references.mjs`.
4. Image names: `compose.yaml` comments, `.env.example`, README and every doc that spells `ghcr.io/jampat000/deluno` become
   `ghcr.io/ejm-digital/deluno` (every version is copied there, so examples naming an older release change too). `scripts/test-readme-docker-examples.sh`
   follows `repository.json` by itself, so it needs no edit; it fails until the README says the new name.
5. README, `docs/` and `*.md` links to `github.com/jampat000/Deluno` (badges, issue links, clone URLs, `docs/ci-standard.md`).
6. Git remotes on every checkout and worktree: `git remote set-url origin https://github.com/EJM-Digital/Deluno.git`.
7. `Directory.Build.props` / package metadata, if any carry the repository URL; `scripts/golden-path` and
   `delete-old-workflow-runs.ps1` need nothing (they read `repository.json` or `GITHUB_REPOSITORY`).
8. Run `node scripts/check-repository-references.mjs` (clean with `PENDING_FLIP` empty) and `npm run ci:check`; grep `docs/` for
   `jampat000` and keep only the deliberate old-release and history mentions.

## After the transfer, in this order

1. The owner transfers `jampat000/Deluno` to `EJM-Digital/Deluno`.
2. Set the remotes (step 6) and push the flip commit once.
3. Run `copy-former-images.yml` (Actions, "Run workflow"). It prints a table of every tag and digest and fails on any missing or
   different one.
4. Verify: pull a few tags by the new name (an old release, a release candidate, a `sha256-*` attestation tag) and compare digests
   with the old name.
5. Make `ghcr.io/ejm-digital/deluno` public (package settings).
6. Delete `ghcr.io/jampat000/deluno`; delete `ghcr.io/jampat000/weir` once Weir says its #1254 has merged.
7. Confirm nothing under the `jampat000` account names Deluno or Weir (repositories, packages, releases, forks, webhooks).
8. Tag rc.30.35 only through `scripts/tag-release.ps1`, after CI and the scenario suite have passed on that commit.

## Checks after the transfer

- Branch rules: the ruleset's bypass actors are still set (the transfer can drop them); a ruleset named for "main is never
  rewritten or deleted" exists (block force-push and deletion on `main`, no bypass for them); the `ci-passed` required check and
  the restriction on creating `v*` tags are present.
- Runners and variables: the repository's Actions variables and secrets are present; every runner the workflows name is
  registered and online; Actions is enabled; Pages/Dependabot settings carried over.
- Old links: `github.com/jampat000/Deluno` redirects (releases.atom, `releases/download/...`, the API).
- An installed rc.30.34 tray updates to the next rc, through its own settings file (which still holds the old address).
- The next release's image is `ghcr.io/ejm-digital/deluno:<version>`, pulls by that name, and its release body names it; every
  earlier version pulls by that name too once the copy has run.
- A Docker install pinned to `ghcr.io/jampat000/deluno` is told in the release notes to switch `DELUNO_IMAGE` to the new name (the old package is deleted).
- Weir's companion download and Deluno's Weir version check still find Weir releases (`EJM-Digital/Weir`).
