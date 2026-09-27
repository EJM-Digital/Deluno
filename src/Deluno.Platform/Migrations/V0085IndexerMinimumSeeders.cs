using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// A per-indexer minimum-seeders floor (#738): a torrent below it is
/// rejected before grab. Null means unrestricted.
/// </summary>
public sealed class V0085IndexerMinimumSeeders : SqliteSqlMigration
{
    public override int Version => 85;

    public override string Name => "indexer_minimum_seeders";

    protected override string Sql =>
        "ALTER TABLE indexer_sources ADD COLUMN minimum_seeders INTEGER NULL;";
}
