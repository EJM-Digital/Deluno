using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Series.Migrations;

/// <summary>What Deluno decided about a show, on the show's row (#309).</summary>
public sealed class V0029SeriesDecisionFacts : SqliteSqlMigration
{
    public override int Version => 29;

    public override string Name => "series_decision_facts";

    protected override string Sql => CatalogueFileFactsMigrationSql.For(
        "series_entries",
        "series_wanted_state",
        "series_id",
        "series_entries",
        CatalogueDecisionFacts.For("series_wanted_state", "series_id"),
        "decision_facts");
}
