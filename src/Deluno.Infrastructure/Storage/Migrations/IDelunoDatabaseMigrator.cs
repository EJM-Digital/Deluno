namespace Deluno.Infrastructure.Storage.Migrations;

public interface IDelunoDatabaseMigrator
{
    Task ApplyAsync(
        string databaseName,
        IReadOnlyList<IDelunoDatabaseMigration> migrations,
        CancellationToken cancellationToken);
}
