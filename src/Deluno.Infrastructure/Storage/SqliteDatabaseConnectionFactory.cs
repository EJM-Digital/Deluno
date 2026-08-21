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

        try
        {
            return await OpenConfiguredConnectionAsync(
                databasePath,
                SqliteOpenMode.ReadWriteCreate,
                requiresInitialization,
                cancellationToken);
        }
        finally
        {
            initializationLock?.Release();
        }
    }

    public async ValueTask<DbConnection> OpenReadOnlyConnectionAsync(
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        var databasePath = GetDatabasePath(databaseName);
        if (!InitializedPaths.ContainsKey(databasePath) || !File.Exists(databasePath))
        {
            await using var writableConnection = await OpenConnectionAsync(databaseName, cancellationToken);
        }

        return await OpenConfiguredConnectionAsync(
            databasePath,
            SqliteOpenMode.ReadOnly,
            applyInitializationPragmas: false,
            cancellationToken);
    }

    private static async Task<DbConnection> OpenConfiguredConnectionAsync(
        string databasePath,
        SqliteOpenMode mode,
        bool applyInitializationPragmas,
        CancellationToken cancellationToken)
    {
        var connectionStringBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,

            // Private cache, deliberately. Shared cache serialises connections
            // inside the process behind table-level locks and raises
            // SQLITE_LOCKED, which busy_timeout does NOT retry — it only covers
            // SQLITE_BUSY. With WAL, private cache is what actually lets readers
            // and the writer run at the same time.
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = true,

            // Command timeout. Must exceed busy_timeout below, or a command
            // waiting legitimately for a lock times out on its own deadline
            // before SQLite has finished waiting.
            DefaultTimeout = 30
        };

        var connection = new SqliteConnection(connectionStringBuilder.ToString());
        await connection.OpenAsync(cancellationToken);

        try
        {
            using var command = connection.CreateCommand();

            // Per-connection pragmas. These do not persist in the file, so
            // every connection has to set them. journal_mode and
            // wal_autocheckpoint are intentionally absent from the read-only
            // path because SQLite cannot change them there.
            command.CommandText = applyInitializationPragmas
                ? """
                  PRAGMA busy_timeout = 5000;
                  PRAGMA journal_mode = WAL;
                  PRAGMA wal_autocheckpoint = 2000;
                  PRAGMA synchronous = NORMAL;
                  PRAGMA temp_store = MEMORY;
                  PRAGMA cache_size = -16000;
                  PRAGMA mmap_size = 268435456;
                  """
                : """
                  PRAGMA busy_timeout = 5000;
                  PRAGMA synchronous = NORMAL;
                  PRAGMA temp_store = MEMORY;
                  PRAGMA cache_size = -16000;
                  PRAGMA mmap_size = 268435456;
                  """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            if (applyInitializationPragmas)
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
}
