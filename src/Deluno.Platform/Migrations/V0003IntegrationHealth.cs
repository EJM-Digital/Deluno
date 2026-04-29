using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public sealed class V0003IntegrationHealth : SqliteSqlMigration
{
    public override int Version => 3;

    public override string Name => "integration_health";

    protected override string Sql =>
        """
        UPDATE indexer_sources
        SET health_status = CASE
            WHEN is_enabled = 0 THEN 'disabled'
            WHEN health_status = 'ready' THEN 'untested'
            WHEN health_status = 'paused' THEN 'disabled'
            WHEN health_status = 'attention' THEN 'degraded'
            ELSE health_status
        END;

        UPDATE download_clients
        SET health_status = CASE
            WHEN is_enabled = 0 THEN 'disabled'
            WHEN health_status = 'ready' THEN 'untested'
            WHEN health_status = 'paused' THEN 'disabled'
            WHEN health_status = 'attention' THEN 'degraded'
            ELSE health_status
        END;

        ALTER TABLE indexer_sources ADD COLUMN last_health_test_utc TEXT NULL;
        ALTER TABLE indexer_sources ADD COLUMN last_health_latency_ms INTEGER NULL;
        ALTER TABLE indexer_sources ADD COLUMN last_health_failure_category TEXT NULL;

        ALTER TABLE download_clients ADD COLUMN last_health_test_utc TEXT NULL;
        ALTER TABLE download_clients ADD COLUMN last_health_latency_ms INTEGER NULL;
        ALTER TABLE download_clients ADD COLUMN last_health_failure_category TEXT NULL;
        """;
}
