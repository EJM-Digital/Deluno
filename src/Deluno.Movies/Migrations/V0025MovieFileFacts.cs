using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Movies.Migrations;

/// <summary>
/// The picked file's facts on the film's own row, so #307's filters seek.
/// Body shared with the series migration; see
/// <see cref="CatalogueFileFactsMigrationSql"/> for why it is generated.
/// </summary>
public sealed class V0025MovieFileFacts : SqliteSqlMigration
{
    public override int Version => 25;

    public override string Name => "movie_file_facts";

    protected override string Sql =>
        CatalogueFileFactsMigrationSql.For("movie_entries", "movie_wanted_state", "movie_id", "movie_entries");
}
