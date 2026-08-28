using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Series.Migrations;

/// <summary>
/// Indexes behind the orders #310 counted.
///
/// <para>No date columns: a show has an air date, which V0020 already indexed,
/// and no cinema or disc release. Offering those orders here would be three
/// controls that can only ever do nothing.</para>
/// </summary>
public sealed class V0025SeriesSortIndexes : SqliteSqlMigration
{
    public override int Version => 25;

    public override string Name => "series_sort_indexes";

    protected override string Sql => CatalogueSortIndexMigrationSql.For("series_entries", "series_entries", []);
}
