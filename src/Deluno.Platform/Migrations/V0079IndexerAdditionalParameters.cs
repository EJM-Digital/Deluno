using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Extra Newznab/Torznab query parameters per indexer (#741), for the
/// site-specific tokens or flags some trackers require beyond the standard
/// set.
/// </summary>
public sealed class V0079IndexerAdditionalParameters : SqliteSqlMigration
{
    public override int Version => 79;

    public override string Name => "indexer_additional_parameters";

    protected override string Sql =>
        "ALTER TABLE indexer_sources ADD COLUMN additional_parameters TEXT NULL;";
}
