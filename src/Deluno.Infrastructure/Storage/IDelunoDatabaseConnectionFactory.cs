using System.Data.Common;

namespace Deluno.Infrastructure.Storage;

public interface IDelunoDatabaseConnectionFactory
{
    string GetDatabasePath(string databaseName);

    ValueTask<DbConnection> OpenConnectionAsync(
        string databaseName,
        CancellationToken cancellationToken = default);

    ValueTask<DbConnection> OpenReadOnlyConnectionAsync(
        string databaseName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A number that moves every time a transaction commits to this database,
    /// from any connection in this process. An answer read from the database
    /// while the number stood at N is out of date once it reads anything else
    /// (#1081).
    /// </summary>
    long GetWriteGeneration(string databaseName);
}
