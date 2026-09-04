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

        // Before the first connection, and that is the whole point. A restore
        // cannot write over databases the application is holding open, so the
        // upload is staged and applied here - the one moment nothing has them.
        var restored = StagedRestore.ApplyPending(storageOptions.Value.DataRoot);
        if (restored.Count > 0)
        {
            logger.LogWarning(
                "Applied a staged restore before opening any database: {Count} file(s) - {Files}.",
                restored.Count,
                string.Join(", ", restored));
        }

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
