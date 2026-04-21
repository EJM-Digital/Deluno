using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Infrastructure.Storage;

public sealed class DelunoStorageBootstrapService(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    IOptions<StoragePathOptions> storageOptions,
    ILogger<DelunoStorageBootstrapService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(storageOptions.Value.DataRoot);

        foreach (var database in DelunoStorageLayout.Databases)
        {
            await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
                database.Key,
                cancellationToken);

            await SetPragmaAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken, scalar: true);
            await SetPragmaAsync(connection, "PRAGMA synchronous = NORMAL;", cancellationToken);
            await SetPragmaAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
        }

        logger.LogInformation(
            "Deluno storage initialized at {DataRoot} with {DatabaseCount} database files.",
            storageOptions.Value.DataRoot,
            DelunoStorageLayout.Databases.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task SetPragmaAsync(
        System.Data.Common.DbConnection connection,
        string sql,
        CancellationToken cancellationToken,
        bool scalar = false)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        if (scalar)
        {
            await command.ExecuteScalarAsync(cancellationToken);
            return;
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
