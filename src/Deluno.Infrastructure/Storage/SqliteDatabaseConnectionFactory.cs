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
    private const long ConfiguredBusyTimeoutMs = 5000;

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

    /// <summary>
    /// Releases the pooled connections to this data root's databases, and to
    /// nobody else's.
    ///
    /// <para>A pooled connection keeps its file open and its write-ahead log
    /// unmerged, so anything that copies, restores or deletes the files first
    /// has to let go of them. The tests used
    /// <c>SqliteConnection.ClearAllPools()</c> for that, which clears every
    /// pool in the process - including those of other tests running in
    /// parallel, mid-query. That is how a test failed with
    /// <c>ObjectDisposedException: SQLitePCL.sqlite3</c> on a connection it
    /// was still using.</para>
    /// </summary>
    public void ClearPools()
    {
        foreach (var database in DatabaseLookup.Values)
        {
            var databasePath = Path.Combine(storageOptions.Value.DataRoot, database.FileName);
            foreach (var mode in new[] { SqliteOpenMode.ReadWriteCreate, SqliteOpenMode.ReadOnly })
            {
                using var connection = new SqliteConnection(BuildConnectionString(databasePath, mode));
                SqliteConnection.ClearPool(connection);
            }
        }
    }

    private static string BuildConnectionString(string databasePath, SqliteOpenMode mode)
        => new SqliteConnectionStringBuilder
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
        }.ToString();

    private static async Task<DbConnection> OpenConfiguredConnectionAsync(
        string databasePath,
        SqliteOpenMode mode,
        bool applyInitializationPragmas,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(BuildConnectionString(databasePath, mode));
        await connection.OpenAsync(cancellationToken);

        try
        {
            // A connection handed back by the pool keeps the pragmas set the first time it
            // was opened, and Microsoft.Data.Sqlite opens every connection with busy_timeout
            // at the command timeout (30 s), never at ConfiguredBusyTimeoutMs. So when this
            // connection already reads back our value, the batch below has been applied and
            // repeating it on every open is seven wasted statements (#1081).
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout;";
            var current = await command.ExecuteScalarAsync(cancellationToken);
            if (current is long currentTimeout && currentTimeout == ConfiguredBusyTimeoutMs)
            {
                if (applyInitializationPragmas)
                {
                    InitializedPaths.TryAdd(databasePath, 0);
                }

                return connection;
            }


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
