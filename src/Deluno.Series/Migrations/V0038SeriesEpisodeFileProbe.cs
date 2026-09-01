using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;

namespace Deluno.Series.Migrations;

/// <summary>
/// Records when each installed episode file was read. A show-level row is a
/// summary and cannot establish evidence for every file in a season pack.
/// </summary>
public sealed class V0038SeriesEpisodeFileProbe : SqliteSqlMigration
{
    public override int Version => 38;

    public override string Name => "series_episode_file_probe";

    protected override string Sql => CatalogueFileProbeMigrationSql.For(
        "episode_entries",
        "episode_entries");
}
