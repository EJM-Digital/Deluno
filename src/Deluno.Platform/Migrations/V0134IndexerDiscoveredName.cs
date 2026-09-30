using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// #1076: an indexer served by Prowlarr or Jackett is named after its tracker, which only
/// the aggregator can say. The name it gave, and the address it gave it for, are kept so a
/// start-up pass (which makes no network calls) can go on using it, and stops using it
/// when the address changes.
/// </summary>
public sealed class V0134IndexerDiscoveredName : SqliteSqlMigration
{
    public override int Version => 134;

    public override string Name => "indexer_discovered_name";

    protected override string Sql =>
        """
        ALTER TABLE indexer_sources ADD COLUMN discovered_name TEXT NULL;
        ALTER TABLE indexer_sources ADD COLUMN discovered_for_url TEXT NULL;
        """;
}
