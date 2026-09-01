using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>Retains the typed provider failure behind the latest subtitle attempt.</summary>
public sealed class V0036SeriesSubtitleFailureDetails : SqliteSqlMigration
{
    public override int Version => 36;

    public override string Name => "series_subtitle_failure_details";

    protected override string Sql =>
        "ALTER TABLE episode_subtitle_attempt ADD COLUMN failure_json TEXT NULL;";
}
