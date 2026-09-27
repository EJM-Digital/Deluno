using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// The monitor mode a list source chose (#848) when it added this series,
/// consulted only while populating that series' own episode catalogue for
/// the first time: it decides which of the newly discovered episodes come
/// out monitored (all of them, only future ones, only the most recent
/// season, and so on) instead of every list-added show always getting every
/// episode monitored.
///
/// NULL (every series added before #848, and any series added outside a
/// list) means "no list monitor mode" — episode monitoring keeps following
/// the existing monitor_new_episodes preset exactly as it does today.
/// </summary>
public sealed class V0047SeriesListMonitorMode : SqliteSqlMigration
{
    public override int Version => 47;

    public override string Name => "series_list_monitor_mode";

    protected override string Sql =>
        "ALTER TABLE series_entries ADD COLUMN list_monitor_mode TEXT NULL;";
}
