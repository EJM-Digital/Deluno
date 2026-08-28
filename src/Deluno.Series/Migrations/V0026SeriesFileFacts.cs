using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Series.Migrations;

/// <summary>
/// The picked file's facts on the show's own row. Body shared with the movie
/// migration of the same name.
/// </summary>
public sealed class V0026SeriesFileFacts : SqliteSqlMigration
{
    public override int Version => 26;

    public override string Name => "series_file_facts";

    protected override string Sql =>
        CatalogueFileFactsMigrationSql.For("series_entries", "series_wanted_state", "series_id", "series_entries");
}
