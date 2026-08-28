using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Movies.Migrations;

/// <summary>
/// What Deluno decided about a film, on the film's row (#309).
/// See <see cref="CatalogueDecisionFacts"/>.
/// </summary>
public sealed class V0028MovieDecisionFacts : SqliteSqlMigration
{
    public override int Version => 28;

    public override string Name => "movie_decision_facts";

    protected override string Sql => CatalogueFileFactsMigrationSql.For(
        "movie_entries",
        "movie_wanted_state",
        "movie_id",
        "movie_entries",
        CatalogueDecisionFacts.For("movie_wanted_state", "movie_id"),
        "decision_facts");
}
