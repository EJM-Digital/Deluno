using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Movies.Migrations;

/// <summary>
/// Certification, collection, original language, and the four ratings on their
/// own — the columns #306 and #319 need to filter and sort by.
///
/// <para>The body is <see cref="CatalogueFactsMigrationSql"/>, shared with the
/// series migration of the same name. Only the table differs.</para>
/// </summary>
public sealed class V0022MovieCatalogueFacts : SqliteSqlMigration
{
    public override int Version => 22;

    public override string Name => "movie_catalogue_facts";

    protected override string Sql => CatalogueFactsMigrationSql.For("movie_entries", "movie_entries");
}
