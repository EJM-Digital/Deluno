using System.Data.Common;
using System.Collections.Concurrent;
using Deluno.Contracts.Manifest;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Deluno.Infrastructure.Storage;

public sealed class SqliteDatabaseConnectionFactory(IOptions<StoragePathOptions> storageOptions)
    : IDelunoDatabaseConnectionFactory
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InitializationLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> InitializedPaths = new(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, DatabaseDescriptor> DatabaseLookup =
        DelunoStorageLayout.Databases.ToDictionary(database => database.Key, StringComparer.OrdinalIgnoreCase);

    public string GetDatabasePath(string databaseName)
    {
        if (!DatabaseLookup.TryGetValue(databaseName, out var database))
        {
            throw new InvalidOperationException($"Unknown Deluno database '{databaseName}'.");
        }

        return Path.Combine(storageOptions.Value.DataRoot, database.FileName);
    }

    public async ValueTask<DbConnection> OpenConnectionAsync(
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        var databasePath = GetDatabasePath(databaseName);
        var requiresInitialization = !InitializedPaths.ContainsKey(databasePath);
        SemaphoreSlim? initializationLock = null;
        if (requiresInitialization)
        {
            initializationLock = InitializationLocks.GetOrAdd(databasePath, static _ => new SemaphoreSlim(1, 1));
            await initializationLock.WaitAsync(cancellationToken);
            requiresInitialization = !InitializedPaths.ContainsKey(databasePath);
        }

        var connectionStringBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 5
        };

        try
        {
            var connection = new SqliteConnection(connectionStringBuilder.ToString());
            await connection.OpenAsync(cancellationToken);

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = requiresInitialization
                    ? """
                      PRAGMA busy_timeout = 5000;
                      PRAGMA journal_mode = WAL;
                      PRAGMA synchronous = NORMAL;
                      PRAGMA temp_store = MEMORY;
                      """
                    : """
                      PRAGMA busy_timeout = 5000;
                      PRAGMA synchronous = NORMAL;
                      PRAGMA temp_store = MEMORY;
                      """;
                await command.ExecuteNonQueryAsync(cancellationToken);
                if (requiresInitialization)
                {
                    InitializedPaths.TryAdd(databasePath, 0);
                }

                return connection;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
        finally
        {
            initializationLock?.Release();
        }
    }
}
