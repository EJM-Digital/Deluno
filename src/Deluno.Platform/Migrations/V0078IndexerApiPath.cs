using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// A non-default Newznab/Torznab API path per indexer (#740), so a server
/// answering somewhere other than <c>/api</c> does not have to be folded into
/// the base URL by hand.
/// </summary>
public sealed class V0078IndexerApiPath : SqliteSqlMigration
{
    public override int Version => 78;

    public override string Name => "indexer_api_path";

    protected override string Sql =>
        "ALTER TABLE indexer_sources ADD COLUMN api_path TEXT NULL;";
}
