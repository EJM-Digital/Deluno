using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

public sealed class V0004SeriesEpisodeQualityTracking : SqliteSqlMigration
{
    public override int Version => 4;

    public override string Name => "series_episode_quality_tracking";

    protected override string Sql =>
        """
        ALTER TABLE series_wanted_state ADD COLUMN prevent_lower_quality_replacements INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE series_wanted_state ADD COLUMN quality_delta_last_decision INTEGER NULL;

        ALTER TABLE episode_wanted_state ADD COLUMN current_quality TEXT NULL;
        ALTER TABLE episode_wanted_state ADD COLUMN target_quality TEXT NULL;
        ALTER TABLE episode_wanted_state ADD COLUMN quality_cutoff_met INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE episode_wanted_state ADD COLUMN prevent_lower_quality_replacements INTEGER NOT NULL DEFAULT 1;
        ALTER TABLE episode_wanted_state ADD COLUMN quality_delta_last_decision INTEGER NULL;

        CREATE INDEX IF NOT EXISTS ix_episode_wanted_state_series_monitored
            ON episode_wanted_state (series_id, library_id, wanted_status);
        """;
}
