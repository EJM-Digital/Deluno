using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public sealed class V0020IndexerRequestInterval : SqliteSqlMigration
{
    public override int Version => 20;

    public override string Name => "indexer_request_interval";

    protected override string Sql =>
        """
        ALTER TABLE indexer_sources ADD COLUMN request_interval_seconds INTEGER NULL;
        """;
}
