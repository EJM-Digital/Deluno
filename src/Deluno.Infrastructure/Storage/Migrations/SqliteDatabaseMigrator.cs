using System.Data.Common;

namespace Deluno.Infrastructure.Storage.Migrations;

public sealed class SqliteDatabaseMigrator(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IDelunoDatabaseMigrator
{
    public async Task ApplyAsync(
        string databaseName,
        IReadOnlyList<IDelunoDatabaseMigration> migrations,
        CancellationToken cancellationToken)
    {
        if (migrations.Count == 0)
        {
            throw new InvalidOperationException($"Database '{databaseName}' has no registered migrations.");
        }

        var orderedMigrations = migrations.OrderBy(migration => migration.Version).ToArray();
        ValidateMigrationSet(databaseName, orderedMigrations);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(databaseName, cancellationToken);
        await EnsureHistoryTableAsync(connection, cancellationToken);

        var applied = await ReadAppliedMigrationsAsync(connection, cancellationToken);
        foreach (var migration in orderedMigrations)
        {
            if (applied.TryGetValue(migration.Version, out var existing))
            {
                if (!string.Equals(existing.Checksum, migration.Checksum, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(existing.Name, migration.Name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Database '{databaseName}' migration {migration.Version} was already applied with a different definition.");
                }

                continue;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await migration.UpAsync(connection, transaction, cancellationToken);
            await RecordAppliedMigrationAsync(connection, transaction, migration, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static void ValidateMigrationSet(string databaseName, IReadOnlyList<IDelunoDatabaseMigration> migrations)
    {
        var duplicateVersions = migrations
            .GroupBy(migration => migration.Version)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicateVersions.Length > 0)
        {
            throw new InvalidOperationException(
                $"Database '{databaseName}' has duplicate migration versions: {string.Join(", ", duplicateVersions)}.");
        }

        var invalidVersion = migrations.FirstOrDefault(migration => migration.Version <= 0);
        if (invalidVersion is not null)
        {
            throw new InvalidOperationException(
                $"Database '{databaseName}' migration '{invalidVersion.Name}' must use a positive version.");
        }
    }

    private static async Task EnsureHistoryTableAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                checksum TEXT NOT NULL,
                applied_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyDictionary<int, AppliedMigration>> ReadAppliedMigrationsAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var applied = new Dictionary<int, AppliedMigration>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT version, name, checksum
            FROM schema_migrations
            ORDER BY version ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            applied[reader.GetInt32(0)] = new AppliedMigration(
                reader.GetString(1),
                reader.GetString(2));
        }

        return applied;
    }

    private async Task RecordAppliedMigrationAsync(
        DbConnection connection,
        DbTransaction transaction,
        IDelunoDatabaseMigration migration,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO schema_migrations (version, name, checksum, applied_utc)
            VALUES (@version, @name, @checksum, @appliedUtc);
            """;

        AddParameter(command, "@version", migration.Version);
        AddParameter(command, "@name", migration.Name);
        AddParameter(command, "@checksum", migration.Checksum);
        AddParameter(command, "@appliedUtc", timeProvider.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record AppliedMigration(string Name, string Checksum);
}
