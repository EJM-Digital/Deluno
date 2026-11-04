using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #1484: the indexer's Minimum seeders is the only seeder rule, and a torrent indexer starts at 1. A torrent
/// indexer with a blank minimum is set to 1 (what the hidden zero-seeder refusal already did, so nothing
/// changes in practice); one with a value already set keeps it. Usenet indexers have no seeder count and stay blank.
/// </summary>
public sealed class V0143IndexerMinimumSeedersDefaultOne : SqliteSqlMigration
{
    public override int Version => 143;

    public override string Name => "indexer_minimum_seeders_default_one";

    protected override string Sql =>
        """
        UPDATE indexer_sources SET minimum_seeders = 1
        WHERE minimum_seeders IS NULL AND LOWER(protocol) <> 'newznab';
        """;
}
