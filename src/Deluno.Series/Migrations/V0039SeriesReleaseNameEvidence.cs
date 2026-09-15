using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>Preserves the exact pre-rename release title beside TV preference evidence.</summary>
public sealed class V0039SeriesReleaseNameEvidence : SqliteSqlMigration
{
    public override int Version => 39;

    public override string Name => "series_release_name_evidence";

    protected override string Sql =>
        "ALTER TABLE media_preference_evaluations ADD COLUMN release_name_evidence TEXT NULL;";
}
