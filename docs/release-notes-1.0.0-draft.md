# Deluno 1.0.0 Release Notes (Draft)

Updated: 2026-05-14

Status: draft for issue #86. Finalize this file against the approved GA candidate tag before publication.

## Summary

Deluno 1.0.0 is the first GA release for the current Windows packaged update model and operational workflow baseline.

Highlights:

- signed Windows packaged release path for `1.x`
- validated installer, upgrade, and rollback matrix
- production-focused backup/restore runbook and recovery drill coverage
- hardened release governance with RC and GA gate criteria

## Upgrade Path

From latest `0.1.x`:

1. Ensure a fresh backup exists (`System > Backups`).
2. Install or update to Deluno 1.0.0 packaged build.
3. Restart when prompted by updater.
4. Validate libraries, indexers, clients, and queue/import health.

Detailed guide:

- [upgrade-guide-0x-to-1x.md](/C:/Users/User/Projects/Deluno/docs/upgrade-guide-0x-to-1x.md)

## Windows Packaging and Updates

- Installer: Velopack setup executable
- In-app updates: `System > Updates`
- Channel: `stable`

### Expected assets for GA

- `Deluno-stable-Setup.exe`
- `Deluno-1.0.0-stable-full.nupkg`
- `releases.stable.json`
- `RELEASES-stable`

## Notable Changes Since 0.1.0

- release/update architecture stabilization across `v0.1.x`
- upgrade flow quality and reliability hardening
- configurable scoring modes including ML-assisted ranking
- improved routing intelligence and monitoring surfaces
- refreshed deployment, troubleshooting, and packaging docs

## Known Issues

Populate with only validated open items at GA cut time:

- `<issue-id>`: `<short impact statement>`

## Support and Troubleshooting

- [TROUBLESHOOTING.md](/C:/Users/User/Projects/Deluno/docs/TROUBLESHOOTING.md)
- [backup-restore-runbook.md](/C:/Users/User/Projects/Deluno/docs/backup-restore-runbook.md)

## Verification Before Publishing

- confirm version strings and tag references
- confirm listed assets exactly match GitHub release assets
- confirm known issues section reflects current open issues
