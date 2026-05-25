using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Downloader.Persistence.Migrations;

public static class DownloaderDatabaseMigrations
{
    public static readonly IReadOnlyList<IDelunoDatabaseMigration> All =
    [
        new V0001InitialSchema()
    ];

    /// <summary>
    /// Initial schema for <c>downloader.db</c>. Matches the schema defined
    /// in <c>docs/exec-plans/active/builtin-downloader-architecture.md</c>:
    /// shared <c>jobs</c> / <c>files</c> / <c>history</c> tables plus
    /// NZB-specific (<c>nzb_servers</c>, <c>nzb_segments</c>,
    /// <c>nzb_server_stats</c>) and torrent-specific
    /// (<c>torrent_metadata</c>, <c>torrent_pieces</c>,
    /// <c>torrent_trackers</c>, <c>torrent_settings</c>) extension tables.
    ///
    /// All credential columns are TEXT (storing <c>ISecretProtector</c>
    /// output, prefixed <c>dp:v1:</c> / <c>aes:v1:</c> / <c>dpapi:v1:</c>);
    /// never BLOB. See architecture doc §Persistence Schema.
    /// </summary>
    private sealed class V0001InitialSchema : SqliteSqlMigration
    {
        public override int Version => 1;
        public override string Name => "initial_schema";

        protected override string Sql =>
            """
            -- Shared --------------------------------------------------------

            CREATE TABLE IF NOT EXISTS jobs (
                id                 TEXT PRIMARY KEY,
                protocol           TEXT NOT NULL CHECK (protocol IN ('nzb','torrent')),
                display_name       TEXT NOT NULL,
                source_path        TEXT NOT NULL,
                source_kind        TEXT NOT NULL,
                category           TEXT,
                priority           INTEGER NOT NULL DEFAULT 0,
                state              TEXT NOT NULL,
                state_reason       TEXT,
                paused             INTEGER NOT NULL DEFAULT 0,
                password_protected TEXT,
                download_dir       TEXT NOT NULL,
                output_dir         TEXT,
                total_bytes        INTEGER NOT NULL,
                downloaded_bytes   INTEGER NOT NULL DEFAULT 0,
                uploaded_bytes     INTEGER NOT NULL DEFAULT 0,
                dispatch_id        TEXT,
                library_id         TEXT,
                created_at         TEXT NOT NULL,
                updated_at         TEXT NOT NULL,
                completed_at       TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_jobs_state_priority
                ON jobs (state, priority);

            CREATE TABLE IF NOT EXISTS files (
                id           TEXT PRIMARY KEY,
                job_id       TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
                file_index   INTEGER NOT NULL,
                name         TEXT NOT NULL,
                is_par2      INTEGER NOT NULL DEFAULT 0,
                is_metadata  INTEGER NOT NULL DEFAULT 0,
                priority     TEXT NOT NULL DEFAULT 'normal',
                total_bytes  INTEGER NOT NULL,
                state        TEXT NOT NULL,
                output_path  TEXT,
                UNIQUE (job_id, file_index)
            );

            CREATE TABLE IF NOT EXISTS history (
                id               TEXT PRIMARY KEY,
                job_id           TEXT NOT NULL,
                protocol         TEXT NOT NULL,
                display_name     TEXT NOT NULL,
                category         TEXT,
                final_state      TEXT NOT NULL,
                total_bytes      INTEGER NOT NULL,
                downloaded_bytes INTEGER NOT NULL,
                uploaded_bytes   INTEGER NOT NULL,
                duration_ms      INTEGER NOT NULL,
                output_path      TEXT,
                failure_reason   TEXT,
                completed_at     TEXT NOT NULL,
                dedupe_key       TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_history_dedupe
                ON history (dedupe_key);

            CREATE INDEX IF NOT EXISTS ix_history_completed
                ON history (completed_at);

            CREATE TABLE IF NOT EXISTS state_transitions (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id       TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
                from_state   TEXT,
                to_state     TEXT NOT NULL,
                reason       TEXT,
                occurred_at  TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_state_transitions_job
                ON state_transitions (job_id, occurred_at);

            -- NZB-specific -------------------------------------------------

            CREATE TABLE IF NOT EXISTS nzb_servers (
                id                  TEXT PRIMARY KEY,
                name                TEXT NOT NULL,
                host                TEXT NOT NULL,
                port                INTEGER NOT NULL,
                use_tls             INTEGER NOT NULL,
                username_protected  TEXT,
                password_protected  TEXT,
                max_connections     INTEGER NOT NULL DEFAULT 8,
                priority            INTEGER NOT NULL DEFAULT 0,
                tier                TEXT NOT NULL CHECK (tier IN ('Primary','Backup','Fill')),
                retention_days      INTEGER,
                enabled             INTEGER NOT NULL DEFAULT 1,
                proxy_url_protected TEXT,
                cert_pin_sha256     TEXT,
                created_at          TEXT NOT NULL,
                updated_at          TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS nzb_segments (
                id             TEXT PRIMARY KEY,
                file_id        TEXT NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                number         INTEGER NOT NULL,
                bytes          INTEGER NOT NULL,
                message_id     TEXT NOT NULL,
                state          TEXT NOT NULL,
                attempts       INTEGER NOT NULL DEFAULT 0,
                last_server_id TEXT,
                last_error     TEXT,
                UNIQUE (file_id, number)
            );

            CREATE INDEX IF NOT EXISTS ix_nzb_segments_state
                ON nzb_segments (state);

            CREATE TABLE IF NOT EXISTS nzb_server_stats (
                server_id    TEXT NOT NULL REFERENCES nzb_servers(id) ON DELETE CASCADE,
                window_start TEXT NOT NULL,
                bytes        INTEGER NOT NULL DEFAULT 0,
                articles_ok  INTEGER NOT NULL DEFAULT 0,
                articles_404 INTEGER NOT NULL DEFAULT 0,
                errors       INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (server_id, window_start)
            );

            -- Torrent-specific ---------------------------------------------

            CREATE TABLE IF NOT EXISTS torrent_metadata (
                job_id           TEXT PRIMARY KEY REFERENCES jobs(id) ON DELETE CASCADE,
                infohash_v1      TEXT,
                infohash_v2      TEXT,
                piece_length     INTEGER NOT NULL,
                piece_count      INTEGER NOT NULL,
                is_private       INTEGER NOT NULL DEFAULT 0,
                fast_resume_blob BLOB,
                comment          TEXT,
                created_by       TEXT,
                creation_date    TEXT
            );

            CREATE TABLE IF NOT EXISTS torrent_pieces (
                job_id      TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
                piece_index INTEGER NOT NULL,
                state       TEXT NOT NULL,
                PRIMARY KEY (job_id, piece_index)
            );

            CREATE TABLE IF NOT EXISTS torrent_trackers (
                id            TEXT PRIMARY KEY,
                job_id        TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
                tier          INTEGER NOT NULL,
                url           TEXT NOT NULL,
                status        TEXT NOT NULL,
                last_announce TEXT,
                last_seeders  INTEGER,
                last_leechers INTEGER,
                last_message  TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_torrent_trackers_job
                ON torrent_trackers (job_id, tier);

            CREATE TABLE IF NOT EXISTS torrent_settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
    }
}
