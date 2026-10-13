#pragma warning disable CS9113 // stub: removed with the implementation
using System.Data.Common;
using Microsoft.Extensions.Logging;

namespace Deluno.Infrastructure.Storage.Migrations;

public sealed class SqliteDatabaseMigrator(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    ILogger<SqliteDatabaseMigrator>? logger = null,
    MigrationLockRetryOptions? lockRetry = null,
    IStartupProgress? progress = null)
    : IDelunoDatabaseMigrator
{
    private readonly MigrationLockRetryOptions _lockRetry = lockRetry ?? new MigrationLockRetryOptions();

    /// <summary>
    /// Applies the pending migrations. A database that another process holds
    /// locked (a backup tool, a slow disk, a previous Deluno still closing) is
    /// retried with a growing pause for up to <see cref="MigrationLockRetryOptions.MaxWait"/>,
    /// with a plain log line, instead of failing start-up on the first busy
    /// answer. If it never clears, the last error is thrown and start-up fails:
    /// Deluno must not run on a database it could not prepare. Each migration
    /// commits on its own, so a retry carries on from the last one that finished.
    /// </summary>
    public async Task ApplyAsync(
        string databaseName,
        IReadOnlyList<IDelunoDatabaseMigration> migrations,
        CancellationToken cancellationToken)
    {
        await SqliteLockRetry.RunAsync(
            () => ApplyOnceAsync(databaseName, migrations, cancellationToken),
            $"Updating the {databaseName} database",
            _lockRetry,
            logger,
            cancellationToken);
    }

    private async Task ApplyOnceAsync(
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
                if (!string.Equals(existing.Name, migration.Name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Database '{databaseName}' migration {migration.Version} was applied as '{existing.Name}' but is now '{migration.Name}'.");
                }

                if (string.Equals(existing.Checksum, migration.Checksum, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Stamped by a build that hashed the raw literal. The SQL itself is
                // unchanged, so adopt the row rather than refusing to start.
                // Ran an earlier definition that was corrected before release.
                if (migration.SupersededChecksums.Contains(existing.Checksum, StringComparer.OrdinalIgnoreCase))
                {
                    await RestampChecksumAsync(connection, migration, cancellationToken);
                    continue;
                }

                if (string.Equals(existing.Checksum, migration.LegacyChecksum, StringComparison.OrdinalIgnoreCase))
                {
                    await RestampChecksumAsync(connection, migration, cancellationToken);
                    continue;
                }

                throw new InvalidOperationException(
                    $"Database '{databaseName}' migration {migration.Version} was already applied with a different definition.");
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await migration.UpAsync(connection, transaction, cancellationToken);
            await RecordAppliedMigrationAsync(connection, transaction, migration, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            if (migration.CompactsDatabaseAfterApply)
            {
                await CompactAsync(connection, databaseName, migration, cancellationToken);
            }
        }
    }

    /// <summary>
    /// One VACUUM after a trimming migration, so the file shrinks. A failure is logged
    /// and does not stop start-up: the data is right, the file is only larger than it
    /// needs to be.
    /// </summary>
    private async Task CompactAsync(
        DbConnection connection,
        string databaseName,
        IDelunoDatabaseMigration migration,
        CancellationToken cancellationToken)
    {
        try
        {
            logger?.LogInformation("Compacting the {Database} database after {Migration}.", databaseName, migration.Name);
            using var vacuum = connection.CreateCommand();
            vacuum.CommandTimeout = 0;
            vacuum.CommandText = "VACUUM;";
            await vacuum.ExecuteNonQueryAsync(cancellationToken);

            using var checkpoint = connection.CreateCommand();
            checkpoint.CommandTimeout = 0;
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync(cancellationToken);

            var dataRoot = Path.GetDirectoryName(databaseConnectionFactory.GetDatabasePath(databaseName));
            if (!string.IsNullOrEmpty(dataRoot))
            {
                DatabaseMaintenanceLog.RecordCompaction(dataRoot, timeProvider.GetUtcNow());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Could not compact the {Database} database after {Migration}; it works, the file is just larger than it needs to be.", databaseName, migration.Name);
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

    /// <summary>
    /// Adopt a row written before checksums were normalised. The migration is not
    /// re-run — only the recorded hash is brought up to date.
    /// </summary>
    private static async Task RestampChecksumAsync(
        DbConnection connection,
        IDelunoDatabaseMigration migration,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE schema_migrations
            SET checksum = @checksum
            WHERE version = @version;
            """;
        AddParameter(command, "@checksum", migration.Checksum);
        AddParameter(command, "@version", migration.Version);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record AppliedMigration(string Name, string Checksum);
}
