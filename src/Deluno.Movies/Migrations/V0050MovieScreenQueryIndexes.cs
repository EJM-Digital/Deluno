using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// #1081: the pick of one wanted-state row per film, which every catalogue page
/// and every chip count makes, ordered its candidates in a temporary tree for
/// each of the film's rows. An index in the pick's own order reads the row it
/// wants first. The dashboard's daily counts of searches filtered a whole history
/// table by day; they now read an index of exactly that.
/// </summary>
public sealed class V0050MovieScreenQueryIndexes : SqliteSqlMigration
{
    public override int Version => 50;

    public override string Name => "movie_screen_query_indexes";

    protected override string Sql =>
        """
        CREATE INDEX IF NOT EXISTS ix_movie_wanted_state_pick
            ON movie_wanted_state (movie_id, has_file DESC, not_wanted_here ASC, quality_cutoff_met ASC, library_id ASC);

        CREATE INDEX IF NOT EXISTS ix_movie_search_history_created_outcome
            ON movie_search_history (created_utc, outcome);
        """;
}
