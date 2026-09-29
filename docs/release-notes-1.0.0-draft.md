# Deluno 1.0.0 Release Notes (Draft)

Updated: 2026-09-29

Status: draft for issue #86, written against candidate `1.0.0-rc.23` (rc.22 was not released; see [the GA release checklist](./ga-release-checklist.md)). The plain-language per-version notes are in `RELEASE_NOTES.md` at the repo root. Finalize this file against the approved GA candidate tag before publication.

## Summary

Deluno 1.0.0 is planned as the first GA release, and its current candidate is `1.0.0-rc.23`, for the current Windows packaged update model and operational workflow baseline. This draft must be finalized only after the approved GA candidate has passed the release gates.

Highlights:

- Windows packaged release path for `1.x`
- installer, upgrade, and rollback evidence (once the clean-machine matrix is complete)
- backup/restore runbook and recovery drill coverage
- RC and GA release governance

## Upgrade Path

From latest `0.1.x`:

1. Ensure a fresh backup exists (`System > Backups`).
2. Install or update to Deluno 1.0.0 packaged build.
3. Restart when prompted by updater.
4. Validate libraries, indexers, clients, and queue/import health.

Detailed guide:

- [Upgrade guide: 0.x to 1.x](./upgrade-guide-0x-to-1x.md)

## Windows Packaging and Updates

- Installer: Velopack setup executable
- In-app updates: `System > Updates`
- Channel: `stable`

### Expected assets for GA

- `Deluno-stable-Setup.exe`
- `Deluno-1.0.0-stable-full.nupkg`
- `releases.stable.json`
- `RELEASES-stable`

### Windows signing note

**Deluno is not code-signed.** Windows SmartScreen shows *"Windows protected
your PC"* on first install; choose **More info** then **Run anyway**. The
warning says the publisher is unverified, not that the download is damaged —
verify it against `SHA256SUMS.txt` if you want certainty.

Stricter configurations (Smart App Control, some managed devices) will refuse
an unsigned installer outright. Use the portable zip fallback where one is
supplied for the release.

## Notable Changes Since 0.1.0

Populate this section from merged changes on the approved candidate. Do not include a capability merely because it was planned or prototyped. The full user-facing list for `1.0.0-rc.23` is its section of `RELEASE_NOTES.md`; the headlines are:

- The installer's tick-box download clients and Weir start on their own, listen on this computer only, and are connected to Deluno and tested automatically; first run goes in two steps.
- Tags decide which titles an indexer, download client or notification connection is used for, with one tag picker everywhere.
- Seerr requests are placed by Deluno's routing rules, and an unplaced 4K request is refused.
- TheTVDB describes TV shows first, and titles show in your chosen language.
- Retention rules gained audio language, watching, collection and playing-now checks.
- Library pills put every title in exactly one pill.

- **A release Deluno judges genuinely bad is now removed from the client and its files deleted by default** (#904). Previously it was only blocklisted and searched again, leaving the dead download and its files behind. Any install that never changed the "remove from client" and "delete files" cleanup settings moves to the new default automatically; one that already set either off keeps that choice. This never applies to a setup problem on your own machine — a full disk, a permissions error, or files missing from disk — which is never treated as a bad release (#880, #881): those stay in the client, keep their files, and are shown as a problem to fix rather than blocklisted, replaced, removed, or deleted.

## Known Issues

Populate with only validated open items at GA cut time:

- `<issue-id>`: `<short impact statement>`

## Support and Troubleshooting

- [Troubleshooting](./TROUBLESHOOTING.md)
- [Backup and restore runbook](./backup-restore-runbook.md)

## Verification Before Publishing

- confirm version strings and tag references
- confirm listed assets exactly match GitHub release assets
- confirm known issues section reflects current open issues
