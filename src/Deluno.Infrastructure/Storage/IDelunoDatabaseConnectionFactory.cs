using System.Data.Common;

namespace Deluno.Infrastructure.Storage;

public interface IDelunoDatabaseConnectionFactory
{
    string GetDatabasePath(string databaseName);

    ValueTask<DbConnection> OpenConnectionAsync(
        string databaseName,
        CancellationToken cancellationToken = default);
}
