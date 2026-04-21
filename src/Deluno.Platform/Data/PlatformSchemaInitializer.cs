using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Platform.Data;

public sealed class PlatformSchemaInitializer(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    ILogger<PlatformSchemaInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS system_settings (
                setting_key TEXT PRIMARY KEY,
                setting_value TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS root_paths (
                root_key TEXT PRIMARY KEY,
                root_path TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS libraries (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                media_type TEXT NOT NULL,
                purpose TEXT NOT NULL,
                root_path TEXT NOT NULL,
                downloads_path TEXT NULL,
                auto_search_enabled INTEGER NOT NULL DEFAULT 1,
                missing_search_enabled INTEGER NOT NULL DEFAULT 1,
                upgrade_search_enabled INTEGER NOT NULL DEFAULT 1,
                search_interval_hours INTEGER NOT NULL DEFAULT 6,
                retry_delay_hours INTEGER NOT NULL DEFAULT 24,
                max_items_per_run INTEGER NOT NULL DEFAULT 25,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS app_connections (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                connection_kind TEXT NOT NULL,
                role TEXT NOT NULL,
                endpoint_url TEXT NULL,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS indexer_sources (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                protocol TEXT NOT NULL,
                privacy TEXT NOT NULL,
                base_url TEXT NOT NULL,
                priority INTEGER NOT NULL DEFAULT 100,
                categories TEXT NOT NULL DEFAULT '',
                tags TEXT NOT NULL DEFAULT '',
                is_enabled INTEGER NOT NULL DEFAULT 1,
                health_status TEXT NOT NULL DEFAULT 'unknown',
                last_health_message TEXT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS download_clients (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                protocol TEXT NOT NULL,
                endpoint_url TEXT NULL,
                category_template TEXT NULL,
                priority INTEGER NOT NULL DEFAULT 100,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                health_status TEXT NOT NULL DEFAULT 'unknown',
                last_health_message TEXT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS library_source_links (
                id TEXT PRIMARY KEY,
                library_id TEXT NOT NULL,
                indexer_id TEXT NOT NULL,
                priority INTEGER NOT NULL DEFAULT 100,
                required_tags TEXT NOT NULL DEFAULT '',
                excluded_tags TEXT NOT NULL DEFAULT '',
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY (library_id) REFERENCES libraries(id) ON DELETE CASCADE,
                FOREIGN KEY (indexer_id) REFERENCES indexer_sources(id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ix_library_source_links_unique
                ON library_source_links (library_id, indexer_id);

            CREATE TABLE IF NOT EXISTS library_download_client_links (
                id TEXT PRIMARY KEY,
                library_id TEXT NOT NULL,
                download_client_id TEXT NOT NULL,
                priority INTEGER NOT NULL DEFAULT 100,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY (library_id) REFERENCES libraries(id) ON DELETE CASCADE,
                FOREIGN KEY (download_client_id) REFERENCES download_clients(id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ix_library_download_client_links_unique
                ON library_download_client_links (library_id, download_client_id);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureLibraryColumnAsync(connection, "missing_search_enabled", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await EnsureLibraryColumnAsync(connection, "upgrade_search_enabled", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await EnsureLibraryColumnAsync(connection, "max_items_per_run", "INTEGER NOT NULL DEFAULT 25", cancellationToken);

        logger.LogInformation(
            "Platform schema is ready at {DatabasePath}.",
            databaseConnectionFactory.GetDatabasePath(DelunoDatabaseNames.Platform));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task EnsureLibraryColumnAsync(
        System.Data.Common.DbConnection connection,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(libraries);";

        var exists = false;
        using (var reader = await check.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE libraries ADD COLUMN {columnName} {columnDefinition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}
