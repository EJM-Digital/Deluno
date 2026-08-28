using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Series.Migrations;

/// <summary>
/// Whether a show's file still matches its tier's size rule.
///
/// <para>The bounds are the episode ones — megabytes, not gigabytes — converted
/// by <c>QualityTierBytes.ForEpisode</c> before they reach this database, so the
/// comparison here is bytes against bytes and neither side has to remember which
/// unit it was handed.</para>
/// </summary>
public sealed class V0028SeriesConformance : SqliteSqlMigration
{
    public override int Version => 28;

    public override string Name => "series_conformance";

    protected override string Sql =>
        CatalogueConformanceMigrationSql.For("series_entries", "series_wanted_state", "series_id", "series_entries");
}
