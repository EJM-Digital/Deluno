using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// A separate, optional seed time for season packs per indexer (#739), used
/// instead of the indexer's normal seed time when the completed download is a
/// season pack.
/// </summary>
public sealed class V0107IndexerSeasonPackSeedTime : SqliteSqlMigration
{
    public override int Version => 107;

    public override string Name => "indexer_season_pack_seed_time";

    protected override string Sql =>
        "ALTER TABLE indexer_sources ADD COLUMN season_pack_seed_for_hours INTEGER NULL;";
}
