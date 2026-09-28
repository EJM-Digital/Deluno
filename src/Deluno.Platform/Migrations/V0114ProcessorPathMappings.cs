using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

/// <summary>
/// Optional path pairs for a processor connection (#962): "Deluno sees" /
/// "processor sees", the same shape download clients already have
/// (V0017DownloadClientPathMappings). A brand-new table, so every existing
/// processor connection keeps behaving exactly as it does today until a pair
/// is added.
/// </summary>
public sealed class V0114ProcessorPathMappings : SqliteSqlMigration
{
    public override int Version => 114;

    public override string Name => "processor_path_mappings";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS processor_path_mappings (
            id TEXT PRIMARY KEY,
            processor_connection_id TEXT NOT NULL,
            deluno_path TEXT NOT NULL,
            processor_path TEXT NOT NULL,
            is_enabled INTEGER NOT NULL DEFAULT 1,
            priority INTEGER NOT NULL DEFAULT 10,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            FOREIGN KEY (processor_connection_id) REFERENCES processor_connections(id) ON DELETE CASCADE
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ix_processor_path_mappings_connection_processor_path
            ON processor_path_mappings (processor_connection_id, processor_path);

        CREATE INDEX IF NOT EXISTS ix_processor_path_mappings_connection_priority
            ON processor_path_mappings (processor_connection_id, is_enabled, priority);
        """;
}
