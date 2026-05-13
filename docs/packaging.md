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

Release gate expectations for tagged builds:

- signing certificate secrets must be present for `1.x.x+` releases
- `Deluno.exe` and setup executables must have valid Authenticode signatures for `1.x.x+` releases
- setup executable, `*.full.nupkg`, and `releases.<channel>.json` are required before publishing the release

Current release channel for production users:

- `stable`

### Manual-to-packaged migration behavior

When moving from a manual Windows run to the packaged Velopack installer:

- Deluno keeps runtime data outside the app binaries, so data root content remains intact.
- Legacy settings under `%ProgramData%\Deluno\data\deluno.json` are detected and migrated to `%LocalAppData%\Deluno\config\deluno.json`.
- In-app apply/restart controls only appear after running the packaged installer path.

## Docker (No In-Place Binary Update)

Docker installs do not perform in-app binary replacement.

Use image/tag updates instead:

```bash
docker pull ghcr.io/<owner>/deluno:<tag>
```

Then recreate containers using your compose or runtime flow.

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
