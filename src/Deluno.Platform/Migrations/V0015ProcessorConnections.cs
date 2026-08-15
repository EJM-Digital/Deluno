using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public sealed class V0015ProcessorConnections : SqliteSqlMigration
{
    public override int Version => 15;

    public override string Name => "processor_connections";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS processor_connections (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL COLLATE NOCASE UNIQUE,
            provider TEXT NOT NULL,
            submission_url TEXT NOT NULL,
            auth_header_name TEXT NOT NULL DEFAULT 'Authorization',
            secret_value TEXT NULL,
            is_enabled INTEGER NOT NULL DEFAULT 1,
            health_status TEXT NOT NULL DEFAULT 'unknown',
            last_health_message TEXT NULL,
            last_health_test_utc TEXT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_processor_connections_enabled
            ON processor_connections (is_enabled, name);
        """;
}
