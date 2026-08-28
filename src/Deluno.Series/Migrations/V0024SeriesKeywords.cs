using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Series.Migrations;

/// <summary>Keywords for a show. Body shared with the movie migration.</summary>
public sealed class V0024SeriesKeywords : SqliteSqlMigration
{
    public override int Version => 24;

    public override string Name => "series_keywords";

    protected override string Sql => CatalogueKeywordsMigrationSql.For("series_entries", "series_entries");
}
