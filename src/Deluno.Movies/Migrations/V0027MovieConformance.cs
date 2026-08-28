using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Movies.Migrations;

/// <summary>
/// Whether a film's file still matches its tier's size rule (#309).
/// Body shared with the series migration.
/// </summary>
public sealed class V0027MovieConformance : SqliteSqlMigration
{
    public override int Version => 27;

    public override string Name => "movie_conformance";

    protected override string Sql =>
        CatalogueConformanceMigrationSql.For("movie_entries", "movie_wanted_state", "movie_id", "movie_entries");
}
