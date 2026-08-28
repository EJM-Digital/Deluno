using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Movies.Migrations;

/// <summary>How often a film has been searched, and how often that found something.</summary>
public sealed class V0029MovieSearchEffort : SqliteSqlMigration
{
    public override int Version => 29;

    public override string Name => "movie_search_effort";

    protected override string Sql => CatalogueFileFactsMigrationSql.For(
        "movie_entries",
        "movie_search_history",
        "movie_id",
        "movie_entries",
        CatalogueSearchEffortFacts.For("movie_search_history", "movie_id"),
        "search_effort");
}
