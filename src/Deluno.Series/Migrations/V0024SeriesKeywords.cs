using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Series.Migrations;

/// <summary>Keywords for a show. Body shared with the movie migration.</summary>
public sealed class V0024SeriesKeywords : SqliteSqlMigration
{
    public override int Version => 24;

    public override string Name => "series_keywords";

    protected override string Sql => CatalogueKeywordsMigrationSql.For("series_entries", "series_entries");

    // #1305: the SQL above stays exactly as shipped; these two steps let it open on a stored keyword list.
    protected override string? BeforeSql => CatalogueKeywordsMigrationSql.RepairBefore("series_entries");

    protected override string? AfterSql => CatalogueKeywordsMigrationSql.RepairAfter("series_entries");
}
