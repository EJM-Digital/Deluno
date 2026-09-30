using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// #1081: the pick of one wanted-state row per show, which every catalogue page
/// and every chip count makes, ordered its candidates in a temporary tree for
/// each of the show's rows. An index in the pick's own order reads the row it
/// wants first. The dashboard's daily counts of searches filtered a whole history
/// table by day; they now read an index of exactly that.
/// </summary>
public sealed class V0053SeriesScreenQueryIndexes : SqliteSqlMigration
{
    public override int Version => 53;

    public override string Name => "series_screen_query_indexes";

    protected override string Sql =>
        """
        CREATE INDEX IF NOT EXISTS ix_series_wanted_state_pick
            ON series_wanted_state (series_id, has_file DESC, quality_cutoff_met ASC, library_id ASC);

        CREATE INDEX IF NOT EXISTS ix_series_search_history_created_outcome
            ON series_search_history (created_utc, outcome);
        """;
}
