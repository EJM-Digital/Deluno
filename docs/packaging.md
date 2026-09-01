# Deluno Packaging and Update Guide

Updated: 2026-05-13

This guide documents the supported distribution paths:

- Windows packaged install and auto-update (Velopack)
- Docker image/tag deployment

## Windows (Supported Installer and Updater)

Deluno now ships as a Velopack package on Windows.

What this provides:

- standard Windows setup executable
- delta updates for smaller downloads
- background download support
- restart-to-apply update flow

### Install location

Default install root:

```text
%LocalAppData%\Deluno
```

This keeps install/update friction low and avoids admin prompts for normal single-user installs.

### Runtime data location

Deluno runtime data is intentionally stored outside the replaceable app payload:

```text
%LocalAppData%\DelunoData
```

This includes:

- SQLite databases
- backups
- protection keys
- logs and runtime state

Do not store mutable runtime data inside the Velopack app directory.

Config path behavior:

- primary settings path: `%LocalAppData%\Deluno\config\deluno.json`
- legacy settings path (read fallback): `%ProgramData%\Deluno\data\deluno.json`
- when legacy settings are detected, Deluno writes a normalized copy to the primary path automatically
- if the legacy `%ProgramData%\Deluno\data` root still contains real runtime data but no surviving config file, Deluno keeps using that legacy data root and writes a primary config file for future launches
- existing explicit data-root values are preserved to avoid breaking upgrades

### In-app updates

Use **System > Updates** in the app.

Supported update behavior modes:

- `Notify only`
- `Download in background`
- `Download and apply on next restart`

Default:

- automatic checks enabled
- background download enabled
- user-initiated restart to finish applying update

Before restart-based apply, Deluno runs a backup gate from the Updates flow.

### Build/release artifacts (Windows)

Release assets include:

- setup executable (`*Setup*.exe`)
- full package (`*.full.nupkg`)
- delta package (`*.delta.nupkg`, when available)
- channel release index (`releases.<channel>.json`)
- portable runtime zip (`*portable.zip`)
- release hashes (`SHA256SUMS.txt`)

Release gate expectations for tagged builds:

- setup executable, portable zip, `SHA256SUMS.txt`, `*.full.nupkg`, and `releases.<channel>.json` are required before publishing the release
- 1.x tagged builds must sign all distributed Windows executables (`artifacts/windows/bin/*.exe` and setup executables), and the release gate must verify every signature as `Valid`
- 0.x prereleases may remain unsigned when the release workflow permits them, but they are not valid evidence for a Windows GA install
- a valid Authenticode signature proves that the artifact was signed and has not been altered; it does not by itself prove that Smart App Control will trust the signer
- SAC enforcement may still block a validly signed first release unless the signer is EV, Microsoft-attested (for example Azure Trusted Signing), or has already established the required reputation; record the certificate type and trust path with the candidate evidence
- never put the production signing certificate on a developer workstation just to make local `dotnet build` output run under SAC

Current release channel for production users:

- `stable`

### Manual-to-packaged migration behavior

When moving from a manual Windows run to the packaged Velopack installer:

- Deluno keeps runtime data outside the app binaries, so data root content remains intact.
- Legacy settings under `%ProgramData%\Deluno\data\deluno.json` are detected and migrated to `%LocalAppData%\Deluno\config\deluno.json`.
- If legacy runtime data still exists under `%ProgramData%\Deluno\data` without a config file, Deluno treats that directory as the upgrade source of truth and writes the new primary config automatically.
- In-app apply/restart controls only appear after running the packaged installer path.

## Docker (No In-Place Binary Update)

Docker installs do not perform in-app binary replacement.

Use versioned image or digest updates instead:

```bash
export DELUNO_IMAGE=ghcr.io/jampat000/deluno:<tag>
docker compose pull deluno
docker compose up -d --no-build
curl --fail http://127.0.0.1:5099/api/health/ready
```

For a repeatable deployment, use an immutable reference:

```text
DELUNO_IMAGE=ghcr.io/jampat000/deluno@sha256:<digest>
```

Record the resolved digest with `docker image inspect` before and after the
rollout. The System > Updates screen shows the image reference/digest supplied
by the container environment and the exact pull, readiness, and rollback
guidance.

Before upgrading, back up `/data`. Rollback is a pull and recreate against the
previous digest with the same volume. Because Deluno migrations are
forward-only, restore the pre-upgrade backup if the older image is not
compatible with the migrated schema.

The Updates screen in Deluno shows Docker guidance and does not expose apply/restart controls for container installs.

### Docker persistence

Always mount persistent app data (for example `/data`) so upgrades do not reset state.

Example compose volume:

```yaml
services:
  deluno:
    volumes:
      - ./artifacts/docker/data:/data
```

## Local Development

For local source-based development and debugging:

- `npm run dev:local` for combined local runtime
- `scripts/publish-windows.ps1` for manual source publish testing

These are development workflows, not the end-user update model.
