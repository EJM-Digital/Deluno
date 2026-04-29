using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

public sealed class V0003SeriesTrackedFiles : SqliteSqlMigration
{
    public override int Version => 3;

    public override string Name => "series_tracked_files";

    protected override string Sql =>
        """
        ALTER TABLE series_wanted_state ADD COLUMN file_path TEXT NULL;
        ALTER TABLE series_wanted_state ADD COLUMN file_size_bytes INTEGER NULL;
        ALTER TABLE series_wanted_state ADD COLUMN imported_utc TEXT NULL;
        ALTER TABLE series_wanted_state ADD COLUMN last_verified_utc TEXT NULL;
        ALTER TABLE series_wanted_state ADD COLUMN missing_detected_utc TEXT NULL;

        ALTER TABLE episode_entries ADD COLUMN file_path TEXT NULL;
        ALTER TABLE episode_entries ADD COLUMN file_size_bytes INTEGER NULL;
        ALTER TABLE episode_entries ADD COLUMN imported_utc TEXT NULL;
        ALTER TABLE episode_entries ADD COLUMN last_verified_utc TEXT NULL;
        ALTER TABLE episode_entries ADD COLUMN missing_detected_utc TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_series_wanted_state_file_path
            ON series_wanted_state (file_path)
            WHERE file_path IS NOT NULL;

        CREATE INDEX IF NOT EXISTS ix_episode_entries_file_path
            ON episode_entries (file_path)
            WHERE file_path IS NOT NULL;
        """;
}
