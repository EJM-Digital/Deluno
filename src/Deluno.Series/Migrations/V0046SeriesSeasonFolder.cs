using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// A per-series override of whether episode files land inside a season
/// subfolder (#850), the way Sonarr lets Season Folder be set per series,
/// including at add time from a list.
///
/// NULL (every existing series, and any new series that never opts in) means
/// "follow the global Media Management season-folder format" — exactly
/// today's behaviour. 1 forces a season subfolder even when the global
/// format is blank; 0 forces episodes flat into the series folder even when
/// the global format is set.
/// </summary>
public sealed class V0046SeriesSeasonFolder : SqliteSqlMigration
{
    public override int Version => 46;

    public override string Name => "series_season_folder";

    protected override string Sql =>
        "ALTER TABLE series_entries ADD COLUMN season_folder INTEGER NULL;";
}
