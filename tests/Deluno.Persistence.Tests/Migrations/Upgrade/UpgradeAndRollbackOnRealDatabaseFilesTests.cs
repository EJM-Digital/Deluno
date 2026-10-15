using Deluno.Api.Backup;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Migrations.Upgrade;

/// <summary>
/// The migrator on real SQLite files: a migration that throws leaves no trace and is tried again at the next start, the
/// oldest supported database ends with the same schema as a new one, and an old backup restored into a data folder is
/// migrated forward without losing a row.
/// </summary>
public sealed class UpgradeAndRollbackOnRealDatabaseFilesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T05:00:00Z");

    private sealed class WritesThenThrowsMigration : SqliteSqlMigration
    {
        public override int Version => 2;

        public override string Name => "writes_then_throws";

        // The first two statements succeed; the third names a table that is not there.
        protected override string Sql =>
            """
            CREATE TABLE half_done (id INTEGER PRIMARY KEY);
            INSERT INTO half_done (id) VALUES (1);
            INSERT INTO table_that_is_not_there (id) VALUES (1);
            """;
    }

    private sealed class WritesThenSucceedsMigration : SqliteSqlMigration
    {
        public override int Version => 2;

        public override string Name => "writes_then_throws";

        protected override string Sql =>
            """
            CREATE TABLE half_done (id INTEGER PRIMARY KEY);
            INSERT INTO half_done (id) VALUES (1);
            """;
    }

    private sealed class FirstMigration : SqliteSqlMigration
    {
        public override int Version => 1;

        public override string Name => "first";

        protected override string Sql => "CREATE TABLE first_done (id INTEGER PRIMARY KEY);";
    }

    [Fact]
    public async Task A_migration_that_throws_rolls_back_whole_is_not_recorded_and_runs_again_at_the_next_start()
    {
        using var storage = TestStorage.Create();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, new FixedTimeProvider(Now));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            migrator.ApplyAsync(DelunoDatabaseNames.Platform, [new FirstMigration(), new WritesThenThrowsMigration()], CancellationToken.None));

        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform))
        {
            Assert.Equal(1L, await MigrationUpgradeHarness.ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.Equal(1L, await MigrationUpgradeHarness.ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations WHERE version = 1;"));
            Assert.Equal(0L, await MigrationUpgradeHarness.ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations WHERE version = 2;"));
            Assert.Equal(0L, await MigrationUpgradeHarness.ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'half_done';"));
        }

        // The next start has the fixed migration: it carries on from the last one that finished.
        await migrator.ApplyAsync(DelunoDatabaseNames.Platform, [new FirstMigration(), new WritesThenSucceedsMigration()], CancellationToken.None);

        await using var after = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);
        Assert.Equal(2L, await MigrationUpgradeHarness.ScalarAsync(after, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal(1L, await MigrationUpgradeHarness.ScalarAsync(after, "SELECT COUNT(*) FROM half_done;"));
    }

    [Theory]
    [InlineData(DelunoDatabaseNames.Platform)]
    [InlineData(DelunoDatabaseNames.Jobs)]
    [InlineData(DelunoDatabaseNames.Movies)]
    [InlineData(DelunoDatabaseNames.Series)]
    [InlineData(DelunoDatabaseNames.Cache)]
    public async Task The_oldest_supported_database_upgraded_to_the_newest_has_the_schema_of_a_new_one(string databaseName)
    {
        var database = MigrationUpgradeHarness.Database(databaseName);
        var oldest = DatabasesOfReleasedVersionsTests.Releases[0];
        var shipped = databaseName switch
        {
            DelunoDatabaseNames.Platform => oldest.Platform,
            DelunoDatabaseNames.Jobs => oldest.Jobs,
            DelunoDatabaseNames.Movies => oldest.Movies,
            DelunoDatabaseNames.Series => oldest.Series,
            _ => oldest.Cache
        };

        using var upgraded = TestStorage.Create();
        using var fresh = TestStorage.Create();
        await MigrationUpgradeHarness.ApplyThroughAsync(upgraded, database, shipped);
        await using (var old = await MigrationUpgradeHarness.OpenAsync(upgraded, databaseName))
        {
            await MigrationUpgradeHarness.SeedEveryTableAsync(old);
        }

        await MigrationUpgradeHarness.ApplyThroughAsync(upgraded, database, database.Latest);
        await MigrationUpgradeHarness.ApplyThroughAsync(fresh, database, database.Latest);

        await using var upgradedConnection = await MigrationUpgradeHarness.OpenAsync(upgraded, databaseName);
        await using var freshConnection = await MigrationUpgradeHarness.OpenAsync(fresh, databaseName);
        var a = await MigrationUpgradeHarness.TakeSnapshotAsync(upgradedConnection);
        var b = await MigrationUpgradeHarness.TakeSnapshotAsync(freshConnection);

        Assert.Equal(b.Columns.Keys.OrderBy(name => name, StringComparer.Ordinal), a.Columns.Keys.OrderBy(name => name, StringComparer.Ordinal));
        var differences = new List<string>();
        foreach (var (table, columns) in b.Columns)
        {
            var upgradedColumns = a.Columns[table].ToDictionary(column => column.Name, StringComparer.Ordinal);
            foreach (var column in columns)
            {
                if (!upgradedColumns.TryGetValue(column.Name, out var other))
                {
                    differences.Add($"{table}.{column.Name} is missing after the upgrade");
                }
                else if (!string.Equals(other.Type, column.Type, StringComparison.OrdinalIgnoreCase) || other.PrimaryKey != column.PrimaryKey)
                {
                    differences.Add($"{table}.{column.Name} is {other.Type}/pk={other.PrimaryKey} after the upgrade but {column.Type}/pk={column.PrimaryKey} in a new database");
                }
            }

            differences.AddRange(a.Columns[table].Where(column => columns.All(other => other.Name != column.Name)).Select(column => $"{table}.{column.Name} exists only after the upgrade"));
        }

        differences.AddRange(b.Indexes.Except(a.Indexes).Select(index => $"index {index} is missing after the upgrade"));
        differences.AddRange(b.Triggers.Except(a.Triggers).Select(trigger => $"trigger {trigger} is missing after the upgrade"));
        Assert.True(differences.Count == 0, $"{databaseName}: {string.Join("; ", differences)}");
    }

    [Fact]
    public async Task An_old_backup_restored_into_a_data_folder_is_migrated_forward_on_start_with_every_row_kept()
    {
        using var source = TestStorage.Create();
        var oldest = DatabasesOfReleasedVersionsTests.Releases[0];
        var shippedBy = new Dictionary<string, int>
        {
            [DelunoDatabaseNames.Platform] = oldest.Platform,
            [DelunoDatabaseNames.Jobs] = oldest.Jobs,
            [DelunoDatabaseNames.Movies] = oldest.Movies,
            [DelunoDatabaseNames.Series] = oldest.Series,
            [DelunoDatabaseNames.Cache] = oldest.Cache
        };
        foreach (var (name, shipped) in shippedBy)
        {
            var database = MigrationUpgradeHarness.Database(name);
            await MigrationUpgradeHarness.ApplyThroughAsync(source, database, shipped);
            await using var connection = await MigrationUpgradeHarness.OpenAsync(source, name);
            await MigrationUpgradeHarness.SeedEveryTableAsync(connection);
        }

        // The backup copies files off disk, so the write-ahead logs have to have reached them.
        source.Factory.ClearPools();
        var backups = new DelunoBackupService(
            Options.Create(new StoragePathOptions { DataRoot = source.DataRoot }),
            new FixedTimeProvider(Now),
            NullLogger<DelunoBackupService>.Instance);
        var backup = await backups.CreateBackupAsync("manual", CancellationToken.None);
        Assert.True(File.Exists(backup.FullPath));

        using var target = TestStorage.Create();
        var restoring = new DelunoBackupService(
            Options.Create(new StoragePathOptions { DataRoot = target.DataRoot }),
            new FixedTimeProvider(Now),
            NullLogger<DelunoBackupService>.Instance);
        await using (var zip = File.OpenRead(backup.FullPath))
        {
            Assert.True((await restoring.RestoreAsync(zip, CancellationToken.None)).Restored);
        }

        StagedRestore.ApplyPending(target.DataRoot);
        target.Factory.ClearPools();

        var problems = new List<string>();
        foreach (var (name, shipped) in shippedBy)
        {
            var database = MigrationUpgradeHarness.Database(name);
            await using var connection = await MigrationUpgradeHarness.OpenAsync(target, name);
            Assert.Equal(shipped, Convert.ToInt32(await MigrationUpgradeHarness.ScalarAsync(connection, "SELECT MAX(version) FROM schema_migrations;")));
            var restored = await MigrationUpgradeHarness.TakeSnapshotAsync(connection);
            Assert.True(restored.Rows.Values.Any(rows => rows.Count > 0), $"{name}: the restored database holds the rows the backup was taken of");

            foreach (var migration in database.Migrations.Where(migration => migration.Version > shipped).OrderBy(migration => migration.Version))
            {
                var before = await MigrationUpgradeHarness.TakeSnapshotAsync(connection);
                await MigrationUpgradeHarness.ApplyOnlyAsync(target, database, migration.Version);
                var after = await MigrationUpgradeHarness.TakeSnapshotAsync(connection);
                var allowed = new HashSet<string>(DataChangingMigrations.Find(name, migration.Version)?.Allowed ?? [], StringComparer.Ordinal);
                problems.AddRange(MigrationUpgradeHarness.LostOrChanged(before, after, allowed).Select(problem => $"{name} V{migration.Version:0000}: {problem}"));
            }

            Assert.Equal(database.Migrations.Count, Convert.ToInt32(await MigrationUpgradeHarness.ScalarAsync(connection, "SELECT COUNT(*) FROM schema_migrations;")));
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
