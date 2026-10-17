using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Migrations;

/// <summary>
/// The migrator refuses a set it cannot trust before it touches the database file: no migrations, a repeated version,
/// a version that is not positive, and a version already applied under another name. Each runs against a real SQLite file.
/// </summary>
public sealed class MigratorRefusalsOnARealDatabaseTests
{
    private static SqliteDatabaseMigrator Migrator(TestStorage storage)
        => new(storage.Factory, new FixedTimeProvider(DateTimeOffset.Parse("2026-10-17T05:00:00Z")));

    private static async Task<long> AppliedCountAsync(TestStorage storage)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'schema_migrations';";
        if (Convert.ToInt64(await exists.ExecuteScalarAsync()) == 0) return 0;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_database_with_no_registered_migrations_is_refused_by_name()
    {
        using var storage = TestStorage.Create();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Migrator(storage).ApplyAsync(DelunoDatabaseNames.Platform, [], CancellationToken.None));

        Assert.Equal("Database 'platform' has no registered migrations.", error.Message);
        Assert.Equal(0, await AppliedCountAsync(storage));
    }

    [Fact]
    public async Task Two_migrations_with_the_same_version_are_refused_and_nothing_is_applied()
    {
        using var storage = TestStorage.Create();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Migrator(storage).ApplyAsync(DelunoDatabaseNames.Platform, [new Probe(3, "first"), new Probe(3, "second")], CancellationToken.None));

        Assert.Equal("Database 'platform' has duplicate migration versions: 3.", error.Message);
        Assert.Equal(0, await AppliedCountAsync(storage));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public async Task A_migration_whose_version_is_not_positive_is_refused_and_nothing_is_applied(int version)
    {
        using var storage = TestStorage.Create();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Migrator(storage).ApplyAsync(DelunoDatabaseNames.Platform, [new Probe(1, "good"), new Probe(version, "bad")], CancellationToken.None));

        Assert.Equal("Database 'platform' migration 'bad' must use a positive version.", error.Message);
        Assert.Equal(0, await AppliedCountAsync(storage));
    }

    [Fact]
    public async Task A_version_already_applied_under_another_name_is_refused_and_the_history_is_left_as_it_was()
    {
        using var storage = TestStorage.Create();
        await Migrator(storage).ApplyAsync(DelunoDatabaseNames.Platform, [new Probe(1, "first_name")], CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Migrator(storage).ApplyAsync(DelunoDatabaseNames.Platform, [new Probe(1, "renamed")], CancellationToken.None));

        Assert.Equal("Database 'platform' migration 1 was applied as 'first_name' but is now 'renamed'.", error.Message);
        Assert.Equal(1, await AppliedCountAsync(storage));
    }

    private sealed class Probe(int version, string name) : SqliteSqlMigration
    {
        public override int Version => version;
        public override string Name => name;
        protected override string Sql => $"CREATE TABLE IF NOT EXISTS probe_{(version < 0 ? "n" : "")}{Math.Abs(version)}_{name} (id INTEGER PRIMARY KEY);";
    }
}
