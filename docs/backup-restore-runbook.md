# Deluno Backup and Restore Runbook

Updated: 2026-05-14

This runbook covers packaged Windows installs and is intended for:

- pre-upgrade safety backups
- rollback after a bad update
- migration to a replacement machine profile

## Scope and Paths

Default packaged Windows paths:

- App install: `%LocalAppData%\Deluno`
- Runtime data: `%LocalAppData%\DelunoData`
- Settings: `%LocalAppData%\Deluno\config\deluno.json`

Backup and restore operations are managed in `System > Backups`.

## Before You Upgrade

1. Open `System > Backups`.
2. Click `Create backup`.
3. Confirm a new backup appears in recent backups.
4. Download the newest backup to external storage.
5. Continue to `System > Updates` only after backup is confirmed.

## Restore Procedure (Same Machine)

Use this when an upgrade regresses behavior or corrupts runtime data.

1. Open `System > Backups`.
2. Upload backup archive and run restore preview.
3. Confirm preview is valid and contains expected files.
4. Run restore.
5. Restart Deluno immediately after restore.
6. Validate health and critical workflows (library scan, queue, imports).

Expected behavior:

- Deluno writes pre-restore copies using `.pre-restore` suffix for files it replaces.
- Restore staging output is stored under the Deluno data root.

## Migration Recovery Procedure (New Machine Profile)

Use this when replacing a machine or moving to a new Windows profile.

1. Install Deluno setup on target machine/profile.
2. Launch once, then close Deluno.
3. Open Deluno and go to `System > Backups`.
4. Upload a backup exported from the source machine.
5. Run restore preview, then restore.
6. Restart Deluno.
7. Validate:
   - libraries and paths resolve correctly in target environment
   - indexers/download clients are healthy
   - queue and import workflows run normally

## Rollback Guidance for Failed Update

If update apply fails or post-update health is degraded:

1. Open `System > Updates` and confirm active version/state.
2. Go to `System > Backups`.
3. Restore the most recent known-good backup.
4. Restart Deluno.
5. Re-validate core workflows before retrying update.
6. If needed, pin to the previous known-good release until root cause is fixed.

## Validation Checklist After Restore

- `/health` and `/api/health/ready` return healthy responses.
- Dashboard and queue load without errors.
- Movies and TV libraries are present.
- One manual search and one import path test complete successfully.
- Backup settings still point to expected backup folder.

## Recovery Drill Evidence

Automated persistence test coverage now includes a second-profile restore drill:

- `tests/Deluno.Persistence.Tests/Api/DelunoBackupServiceTests.cs`

Scenario covered:

- create backup from source profile data root
- restore backup into a separate target profile data root
- verify restored files and `.pre-restore` preservation
