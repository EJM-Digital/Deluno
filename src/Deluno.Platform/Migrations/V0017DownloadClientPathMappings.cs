using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public sealed class V0017DownloadClientPathMappings : SqliteSqlMigration
{
    public override int Version => 17;

    public override string Name => "download_client_path_mappings";

    protected override string Sql =>
        """
        CREATE TABLE IF NOT EXISTS download_client_path_mappings (
            id TEXT PRIMARY KEY,
            download_client_id TEXT NOT NULL,
            remote_path TEXT NOT NULL,
            local_path TEXT NOT NULL,
            is_enabled INTEGER NOT NULL DEFAULT 1,
            priority INTEGER NOT NULL DEFAULT 10,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            FOREIGN KEY (download_client_id) REFERENCES download_clients(id) ON DELETE CASCADE
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ix_download_client_path_mappings_client_remote
            ON download_client_path_mappings (download_client_id, remote_path);

        CREATE INDEX IF NOT EXISTS ix_download_client_path_mappings_client_priority
            ON download_client_path_mappings (download_client_id, is_enabled, priority);
        """;
}
