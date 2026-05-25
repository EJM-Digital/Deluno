using Deluno.Downloader.Persistence.Migrations;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Downloader.Persistence;

/// <summary>
/// Applies <see cref="DownloaderDatabaseMigrations"/> on startup, plus
/// WAL tuning per the architecture doc (PRAGMA wal_autocheckpoint=1000)
/// to keep the WAL bounded under heavy article-completion write traffic.
/// </summary>
public sealed class DownloaderSchemaInitializer(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    IDelunoDatabaseMigrator migrator,
    ILogger<DownloaderSchemaInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await migrator.ApplyAsync(
            DelunoDatabaseNames.Downloader,
            DownloaderDatabaseMigrations.All,
            cancellationToken);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Downloader, cancellationToken);
        await using (var pragma = connection.CreateCommand())
        {
            // Keep the WAL from growing unbounded under heavy segment-
            // completion write traffic. A 5GB NZB can otherwise produce
            // a multi-GB WAL that survives restarts. See architecture
            // doc §Risk Register (SQLite WAL growth).
            pragma.CommandText = "PRAGMA wal_autocheckpoint = 1000;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        logger.LogInformation(
            "Downloader database migrations are current at {DatabasePath}.",
            databaseConnectionFactory.GetDatabasePath(DelunoDatabaseNames.Downloader));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
