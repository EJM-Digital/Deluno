using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// #1077: a search or grab is filed under its indexer's id, so renaming the indexer does not split its history. Filled in by the start-up backfill.
/// </summary>
public sealed class V0048MovieSearchHistoryIndexerId : SqliteSqlMigration
{
    public override int Version => 48;

    public override string Name => "movie_search_history_indexer_id";

    protected override string Sql =>
        """
        ALTER TABLE movie_search_history ADD COLUMN indexer_id TEXT NULL;
        """;
}
