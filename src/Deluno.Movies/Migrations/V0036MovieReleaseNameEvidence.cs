using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>Preserves the exact pre-rename release title beside movie preference evidence.</summary>
public sealed class V0036MovieReleaseNameEvidence : SqliteSqlMigration
{
    public override int Version => 36;

    public override string Name => "movie_release_name_evidence";

    protected override string Sql =>
        "ALTER TABLE media_preference_evaluations ADD COLUMN release_name_evidence TEXT NULL;";
}
