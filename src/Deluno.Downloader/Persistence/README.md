# Persistence

Shared SQLite schema + repositories for `downloader.db`. Per the
architecture doc:

- Shared tables: `jobs`, `files`, `history`.
- NZB-specific extension tables: `nzb_servers`, `nzb_segments`,
  `nzb_server_stats`.
- Torrent-specific extension tables: `torrent_metadata`,
  `torrent_pieces`, `torrent_trackers`, `torrent_settings`.
- All credential columns are TEXT (storing `ISecretProtector` output,
  prefixed `dp:v1:`), never BLOB.
- WAL tuning: `PRAGMA wal_autocheckpoint=1000` + periodic
  `wal_checkpoint(TRUNCATE)` to avoid WAL bloat under high write
  volume.
- Migrations live in `Migrations/`, follow
  `Deluno.Integrations.Migrations` conventions.

Lands in Phase 2.
