using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Series.Migrations;

/// <summary>How often a show has been searched, and how often that found something.</summary>
public sealed class V0030SeriesSearchEffort : SqliteSqlMigration
{
    public override int Version => 30;

    public override string Name => "series_search_effort";

    protected override string Sql => CatalogueFileFactsMigrationSql.For(
        "series_entries",
        "series_search_history",
        "series_id",
        "series_entries",
        CatalogueSearchEffortFacts.For("series_search_history", "series_id"),
        "search_effort");
}
