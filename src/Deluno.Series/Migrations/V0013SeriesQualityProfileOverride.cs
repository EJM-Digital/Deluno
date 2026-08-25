using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// The per-series quality profile the code has always written and the table has
/// never had. The series half of the same defect as
/// <c>V0013MovieQualityProfileOverride</c>.
///
/// <c>SqliteSeriesCatalogRepository.UpdateQualityProfileAsync</c> issues
/// <c>UPDATE series_entries SET quality_profile_id = …</c> against a column no
/// migration created, so assigning a profile to a series — individually, in
/// bulk, or through an import list configured with one — threw
/// <c>SQLite Error 1: 'no such column: quality_profile_id'</c>.
///
/// Nullable with no default: null means the series follows its library's
/// profile, which is what every existing row already does.
/// </summary>
public sealed class V0013SeriesQualityProfileOverride : SqliteSqlMigration
{
    public override int Version => 13;

    public override string Name => "series_quality_profile_override";

    protected override string Sql =>
        """
        ALTER TABLE series_entries ADD COLUMN quality_profile_id TEXT NULL;
        """;
}
