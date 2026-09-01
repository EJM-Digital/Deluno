using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Series.Migrations;

/// <summary>Durable typed preference evidence for installed TV files.</summary>
public sealed class V0034SeriesPreferenceEvaluations : SqliteSqlMigration
{
    public override int Version => 34;

    public override string Name => "series_preference_evaluations";

    protected override string Sql => PreferenceEvaluationMigrationSql.For(
        "media_preference_evaluations",
        "series_entries",
        "series_id",
        "series_preference_evaluations");
}
