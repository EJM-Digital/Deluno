# Upgrade Guide: Deluno 0.x to 1.x

Updated: 2026-05-14

This guide is for users upgrading from packaged `0.1.x` builds to `1.x` GA.

## Before You Upgrade

1. Open Deluno and confirm current version in `System > Updates`.
2. Create a manual backup in `System > Backups`.
3. Download and keep the newest backup archive outside the app machine.
4. Confirm available disk space for package apply and restart.

## Upgrade Steps

1. Download the `1.x` setup from GitHub Releases.
2. Run setup and complete install/update.
3. Open Deluno and go to `System > Updates`.
4. If update is staged, use restart/apply flow.
5. After restart, confirm reported version is `1.x`.

If the Windows artifact is unsigned for this release cycle, Windows may show additional trust prompts. Verify the release URL and artifact hash/source before continuing.

## Post-Upgrade Validation

Check these in order:

1. Health endpoints respond:
   - `/health`
   - `/api/health/ready`
2. Libraries load with expected content counts.
3. Indexers and download clients show healthy connectivity.
4. Queue and activity screens load without errors.
5. One manual search and one import workflow complete.

## If Upgrade Fails

1. Do not continue repeated retries without checking status.
2. Open `System > Updates` and capture current state details.
3. Go to `System > Backups` and restore last known-good backup.
4. Restart Deluno after restore.
5. Re-validate health and core workflows.
6. If still blocked, revert to previous known-good release and report details.

## If Data Looks Wrong After Upgrade

1. Verify Deluno is using expected data root path.
2. Verify library paths are valid from Deluno runtime environment.
3. Check `System > Backups` for latest backup and restore if required.
4. Review troubleshooting guide for path-mapping and runtime checks.

## References

- [packaging.md](/C:/Users/User/Projects/Deluno/docs/packaging.md)
- [DEPLOYMENT.md](/C:/Users/User/Projects/Deluno/docs/DEPLOYMENT.md)
- [TROUBLESHOOTING.md](/C:/Users/User/Projects/Deluno/docs/TROUBLESHOOTING.md)
- [backup-restore-runbook.md](/C:/Users/User/Projects/Deluno/docs/backup-restore-runbook.md)
