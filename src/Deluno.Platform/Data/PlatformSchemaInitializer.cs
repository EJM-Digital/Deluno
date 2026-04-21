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
                search_interval_hours INTEGER NOT NULL DEFAULT 6,
                retry_delay_hours INTEGER NOT NULL DEFAULT 24,
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
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);

        logger.LogInformation(
            "Platform schema is ready at {DatabasePath}.",
            databaseConnectionFactory.GetDatabasePath(DelunoDatabaseNames.Platform));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
