using System.Data.Common;
using Deluno.Contracts.Manifest;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Deluno.Infrastructure.Storage;

public sealed class SqliteDatabaseConnectionFactory(IOptions<StoragePathOptions> storageOptions)
    : IDelunoDatabaseConnectionFactory
{
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
        var connectionStringBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = GetDatabasePath(databaseName),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 5
        };

        var connection = new SqliteConnection(connectionStringBuilder.ToString());
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }
}
