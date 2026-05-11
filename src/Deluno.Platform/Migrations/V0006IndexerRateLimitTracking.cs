using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public sealed class V0006IndexerRateLimitTracking : SqliteSqlMigration
{
    public override int Version => 6;

    public override string Name => "indexer_rate_limit_tracking";

    protected override string Sql =>
        """
        ALTER TABLE indexer_sources ADD COLUMN consecutive_failures INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE indexer_sources ADD COLUMN rate_limited_until_utc TEXT NULL;
        ALTER TABLE indexer_sources ADD COLUMN disabled_reason TEXT NULL;

        ALTER TABLE download_clients ADD COLUMN consecutive_failures INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE download_clients ADD COLUMN rate_limited_until_utc TEXT NULL;
        ALTER TABLE download_clients ADD COLUMN disabled_reason TEXT NULL;
        """;
}
