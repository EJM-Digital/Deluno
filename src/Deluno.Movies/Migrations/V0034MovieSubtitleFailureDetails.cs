using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>Retains the typed provider failure behind the latest subtitle attempt.</summary>
public sealed class V0034MovieSubtitleFailureDetails : SqliteSqlMigration
{
    public override int Version => 34;

    public override string Name => "movie_subtitle_failure_details";

    protected override string Sql =>
        "ALTER TABLE movie_subtitle_attempt ADD COLUMN failure_json TEXT NULL;";
}
