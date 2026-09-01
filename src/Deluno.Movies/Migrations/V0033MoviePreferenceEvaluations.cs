using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Movies.Migrations;

/// <summary>Durable typed preference evidence for installed movie files.</summary>
public sealed class V0033MoviePreferenceEvaluations : SqliteSqlMigration
{
    public override int Version => 33;

    public override string Name => "movie_preference_evaluations";

    protected override string Sql => PreferenceEvaluationMigrationSql.For(
        "media_preference_evaluations",
        "movie_entries",
        "movie_id",
        "movie_preference_evaluations");
}
