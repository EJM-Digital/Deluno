using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Movies.Migrations;

/// <summary>What the media probe still owes, for films. See <see cref="CatalogueFileProbeMigrationSql"/>.</summary>
public sealed class V0030MovieFileProbe : SqliteSqlMigration
{
    public override int Version => 30;

    public override string Name => "movie_file_probe";

    protected override string Sql => CatalogueFileProbeMigrationSql.For("movie_wanted_state", "movie_wanted_state");
}
