using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Migrations.Upgrade;

/// <summary>
/// #1274. For every migration of every database: build the database at the migration before it, put a row in
/// every table, apply it, and check that no row is lost, no value changed and every new column reads its default.
/// A migration that does change existing rows must be listed in <see cref="DataChangingMigrations"/> with the
/// hand-written tests that prove what the rows mean afterwards.
/// </summary>
public sealed class EveryMigrationKeepsItsRowsTests
{
    public static IEnumerable<object[]> Migrations() => MigrationUpgradeHarness.EveryMigration();

    [Theory]
    [MemberData(nameof(Migrations))]
    public async Task Applying_a_migration_on_a_database_that_holds_rows_keeps_every_row_and_its_meaning(string databaseName, int version)
    {
        using var storage = TestStorage.Create();
        var database = MigrationUpgradeHarness.Database(databaseName);
        var migration = database.Migrations.Single(m => m.Version == version);
        var previous = MigrationUpgradeHarness.PreviousVersion(database, version);

        await MigrationUpgradeHarness.ApplyThroughAsync(storage, database, previous);
        await using var connection = await MigrationUpgradeHarness.OpenAsync(storage, databaseName);
        await MigrationUpgradeHarness.SeedEveryTableAsync(connection);
        var before = await MigrationUpgradeHarness.TakeSnapshotAsync(connection);

        await MigrationUpgradeHarness.ApplyOnlyAsync(storage, database, version);
        var after = await MigrationUpgradeHarness.TakeSnapshotAsync(connection);

        var change = DataChangingMigrations.Find(databaseName, version);
        var allowed = new HashSet<string>(change?.Allowed ?? [], StringComparer.Ordinal);
        var problems = MigrationUpgradeHarness.LostOrChanged(before, after, allowed);
        problems.AddRange(await MigrationUpgradeHarness.AddedColumnsHoldTheirDefaultAsync(connection, before, after, allowed));

        Assert.True(problems.Count == 0, $"{databaseName} V{version:0000} {migration.Name}: {string.Join("; ", problems)}");

        var recorded = await MigrationUpgradeHarness.RowsAsync(connection, $"SELECT name, checksum FROM schema_migrations WHERE version = {version};");
        Assert.Equal(migration.Name, recorded.Single()["name"]);
        Assert.Equal(migration.Checksum, recorded.Single()["checksum"]);
    }
}
