using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Movies.Migrations;

/// <summary>Indexes behind the orders #310 counted. Body shared with the series migration.</summary>
public sealed class V0024MovieSortIndexes : SqliteSqlMigration
{
    public override int Version => 24;

    public override string Name => "movie_sort_indexes";

    protected override string Sql => CatalogueSortIndexMigrationSql.For(
        "movie_entries",
        "movie_entries",
        ["in_cinemas_date", "digital_release_date", "physical_release_date"]);
}
