using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Movies.Migrations;

/// <summary>Keywords for a film. Body shared with the series migration.</summary>
public sealed class V0023MovieKeywords : SqliteSqlMigration
{
    public override int Version => 23;

    public override string Name => "movie_keywords";

    protected override string Sql => CatalogueKeywordsMigrationSql.For("movie_entries", "movie_entries");
}
