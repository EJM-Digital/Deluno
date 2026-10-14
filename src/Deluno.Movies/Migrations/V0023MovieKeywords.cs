using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Movies.Migrations;

/// <summary>Keywords for a film. Body shared with the series migration.</summary>
public sealed class V0023MovieKeywords : SqliteSqlMigration
{
    public override int Version => 23;

    public override string Name => "movie_keywords";

    protected override string Sql => CatalogueKeywordsMigrationSql.For("movie_entries", "movie_entries");

    // #1305: the SQL above stays exactly as shipped; these two steps let it open on a stored keyword list.
    protected override string? BeforeSql => CatalogueKeywordsMigrationSql.RepairBefore("movie_entries");

    protected override string? AfterSql => CatalogueKeywordsMigrationSql.RepairAfter("movie_entries");
}
