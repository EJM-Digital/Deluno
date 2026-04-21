using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Integrations;

public sealed class CacheSchemaInitializer(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    ILogger<CacheSchemaInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Cache,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS provider_payload_cache (
                cache_key TEXT PRIMARY KEY,
                provider_name TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                fetched_utc TEXT NOT NULL,
                expires_utc TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_provider_payload_cache_provider_expires
                ON provider_payload_cache (provider_name, expires_utc);

            CREATE TABLE IF NOT EXISTS provider_etags (
                provider_name TEXT NOT NULL,
                resource_key TEXT NOT NULL,
                etag TEXT NULL,
                last_modified TEXT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (provider_name, resource_key)
            );

            CREATE TABLE IF NOT EXISTS search_result_cache (
                cache_key TEXT PRIMARY KEY,
                media_type TEXT NOT NULL,
                query_text TEXT NOT NULL,
                result_json TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                expires_utc TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_search_result_cache_media_expires
                ON search_result_cache (media_type, expires_utc);

            CREATE TABLE IF NOT EXISTS artwork_cache (
                cache_key TEXT PRIMARY KEY,
                media_type TEXT NOT NULL,
                remote_url TEXT NOT NULL,
                local_path TEXT NULL,
                fetched_utc TEXT NOT NULL,
                expires_utc TEXT NULL
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);

        logger.LogInformation(
            "Cache schema is ready at {DatabasePath}.",
            databaseConnectionFactory.GetDatabasePath(DelunoDatabaseNames.Cache));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
