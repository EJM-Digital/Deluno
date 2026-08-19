using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Movies.Migrations;

/// <summary>
/// Supports the catalogue list's sort order.
///
/// <c>SqliteMovieCatalogRepository.ListAsync</c> ends
/// <c>ORDER BY m.created_utc DESC, m.title ASC</c>, and until now
/// <c>movie_entries</c> carried no index covering it — so every list request
/// sorted the entire table from scratch. That cost grows with the library,
/// which is the shape this index exists to remove. Column order and direction
/// deliberately mirror the query exactly so SQLite can walk the index instead
/// of sorting.
/// </summary>
public sealed class V0008MovieCatalogueListIndex : SqliteSqlMigration
{
    public override int Version => 8;

    public override string Name => "movie_catalogue_list_index";

    protected override string Sql =>
        """
        CREATE INDEX IF NOT EXISTS ix_movie_entries_created_title
            ON movie_entries (created_utc DESC, title ASC);
        """;
}
