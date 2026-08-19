using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Series.Migrations;

/// <summary>
/// The series twin of <c>V0008MovieCatalogueListIndex</c>.
///
/// <c>SqliteSeriesCatalogRepository.ListAsync</c> ends
/// <c>ORDER BY s.created_utc DESC, s.title ASC</c> with no index covering it,
/// so every list request sorted the whole table. Column order and direction
/// mirror the query exactly.
/// </summary>
public sealed class V0008SeriesCatalogueListIndex : SqliteSqlMigration
{
    public override int Version => 8;

    public override string Name => "series_catalogue_list_index";

    protected override string Sql =>
        """
        CREATE INDEX IF NOT EXISTS ix_series_entries_created_title
            ON series_entries (created_utc DESC, title ASC);
        """;
}
