using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// A per-series "monitor new episodes" preset (#801), read on refresh so a
/// newly discovered episode comes out monitored or not according to what
/// this series asked for, instead of always monitored.
///
/// Defaults every existing series to on, matching current behaviour: before
/// this preset existed, a newly discovered episode was always monitored.
/// </summary>
public sealed class V0044SeriesMonitorNewEpisodesPreset : SqliteSqlMigration
{
    public override int Version => 44;

    public override string Name => "series_monitor_new_episodes_preset";

    protected override string Sql =>
        """
        ALTER TABLE series_entries ADD COLUMN monitor_new_episodes INTEGER NOT NULL DEFAULT 1;
        """;
}
