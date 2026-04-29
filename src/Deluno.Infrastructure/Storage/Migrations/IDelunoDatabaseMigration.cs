using System.Data.Common;

namespace Deluno.Infrastructure.Storage.Migrations;

public interface IDelunoDatabaseMigration
{
    int Version { get; }

    string Name { get; }

    string Checksum { get; }

    Task UpAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken);
}
