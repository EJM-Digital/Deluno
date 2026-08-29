using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Series.Migrations;

/// <summary>What the media probe still owes, for shows.</summary>
public sealed class V0031SeriesFileProbe : SqliteSqlMigration
{
    public override int Version => 31;

    public override string Name => "series_file_probe";

    protected override string Sql => CatalogueFileProbeMigrationSql.For("series_wanted_state", "series_wanted_state");
}
