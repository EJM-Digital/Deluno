using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Jobs.Migrations;

public sealed class V0003DownloadOutcomeTracking : SqliteSqlMigration
{
    public override int Version => 3;

    public override string Name => "download_outcome_tracking";

    protected override string Sql =>
        """
        -- Add grab outcome columns
        ALTER TABLE download_dispatches ADD COLUMN grab_status TEXT NULL;
        ALTER TABLE download_dispatches ADD COLUMN grab_attempted_utc TEXT NULL;
        ALTER TABLE download_dispatches ADD COLUMN grab_response_code INTEGER NULL;
        ALTER TABLE download_dispatches ADD COLUMN grab_message TEXT NULL;
        ALTER TABLE download_dispatches ADD COLUMN grab_failure_code TEXT NULL;
        ALTER TABLE download_dispatches ADD COLUMN grab_response_json TEXT NULL;

        -- Add detection columns (from polling)
        ALTER TABLE download_dispatches ADD COLUMN detected_utc TEXT NULL;
        ALTER TABLE download_dispatches ADD COLUMN torrent_hash_or_item_id TEXT NULL;
        ALTER TABLE download_dispatches ADD COLUMN downloaded_bytes INTEGER NULL;

        -- Add import outcome columns
        ALTER TABLE download_dispatches ADD COLUMN import_status TEXT NULL;
        ALTER TABLE download_dispatches ADD COLUMN import_detected_utc TEXT NULL;
        ALTER TABLE download_dispatches ADD COLUMN import_completed_utc TEXT NULL;
        ALTER TABLE download_dispatches ADD COLUMN imported_file_path TEXT NULL;
        ALTER TABLE download_dispatches ADD COLUMN import_failure_code TEXT NULL;
        ALTER TABLE download_dispatches ADD COLUMN import_failure_message TEXT NULL;

        -- Add circuit breaker column
        ALTER TABLE download_dispatches ADD COLUMN circuit_open_until_utc TEXT NULL;

        -- Create indices for fast querying
        CREATE INDEX IF NOT EXISTS ix_download_dispatches_grab_status
            ON download_dispatches (download_client_id, grab_status, grab_attempted_utc DESC);

        CREATE INDEX IF NOT EXISTS ix_download_dispatches_detection_status
            ON download_dispatches (download_client_id, detected_utc DESC)
            WHERE detected_utc IS NOT NULL;

        CREATE INDEX IF NOT EXISTS ix_download_dispatches_import_status
            ON download_dispatches (import_status, import_completed_utc DESC)
            WHERE import_status IS NOT NULL;

        CREATE INDEX IF NOT EXISTS ix_download_dispatches_unresolved
            ON download_dispatches (download_client_id, grab_attempted_utc DESC)
            WHERE grab_status = 'succeeded' AND detected_utc IS NULL;

        CREATE INDEX IF NOT EXISTS ix_download_dispatches_imported_file
            ON download_dispatches (imported_file_path)
            WHERE imported_file_path IS NOT NULL;

        CREATE TABLE IF NOT EXISTS download_dispatch_timeline (
            id TEXT PRIMARY KEY,
            dispatch_id TEXT NOT NULL,
            event_type TEXT NOT NULL,
            timestamp TEXT NOT NULL,
            details_json TEXT NULL,
            created_utc TEXT NOT NULL,
            FOREIGN KEY (dispatch_id) REFERENCES download_dispatches (id)
        );

        CREATE INDEX IF NOT EXISTS ix_download_dispatch_timeline_dispatch
            ON download_dispatch_timeline (dispatch_id, timestamp DESC);

        CREATE INDEX IF NOT EXISTS ix_download_dispatch_timeline_event
            ON download_dispatch_timeline (event_type, timestamp DESC);
        """;
}
