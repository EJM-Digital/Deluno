using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Migrations;

/// <summary>#1080: start-up waits for a locked database instead of failing on the first busy answer.</summary>
public sealed class MigrationLockRetryTests
{
    private static readonly MigrationLockRetryOptions Quick = new()
    {
        MaxWait = TimeSpan.FromSeconds(5),
        FirstPause = TimeSpan.FromMilliseconds(10),
        MaxPause = TimeSpan.FromMilliseconds(20)
    };

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task A_busy_or_locked_answer_is_retried_until_it_clears(int code)
    {
        var attempts = 0;
        await SqliteLockRetry.RunAsync(
            () =>
            {
                attempts++;
                return attempts < 3 ? throw new SqliteException("database is locked", code) : Task.CompletedTask;
            },
            "Test step",
            Quick,
            null,
            CancellationToken.None);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task A_lock_that_never_clears_fails_after_the_limit_with_the_original_error()
    {
        var attempts = 0;
        var options = new MigrationLockRetryOptions
        {
            MaxWait = TimeSpan.FromMilliseconds(200),
            FirstPause = TimeSpan.FromMilliseconds(20),
            MaxPause = TimeSpan.FromMilliseconds(40)
        };

        var failure = await Assert.ThrowsAsync<SqliteException>(() => SqliteLockRetry.RunAsync(
            () =>
            {
                attempts++;
                throw new SqliteException("database is locked", 5);
            },
            "Test step",
            options,
            null,
            CancellationToken.None));

        Assert.Equal(5, failure.SqliteErrorCode);
        Assert.True(attempts > 1);
    }

    [Fact]
    public async Task Any_other_database_error_is_not_retried()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<SqliteException>(() => SqliteLockRetry.RunAsync(
            () =>
            {
                attempts++;
                throw new SqliteException("disk I/O error", 10);
            },
            "Test step",
            Quick,
            null,
            CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task The_migrator_waits_out_a_write_lock_held_longer_than_one_busy_timeout()
    {
        using var storage = TestStorage.Create();
        var path = storage.Factory.GetDatabasePath(DelunoDatabaseNames.Platform);
        var migrator = new SqliteDatabaseMigrator(
            storage.Factory,
            TimeProvider.System,
            null,
            new MigrationLockRetryOptions { MaxWait = TimeSpan.FromSeconds(60), FirstPause = TimeSpan.FromMilliseconds(200), MaxPause = TimeSpan.FromSeconds(1) });

        // Prepare the database first so the held lock is a write lock on a real file.
        await migrator.ApplyAsync(DelunoDatabaseNames.Platform, [new ProbeMigration(1, "one", "CREATE TABLE probe_one (id INTEGER);")], CancellationToken.None);

        using var holder = new SqliteConnection($"Data Source={path};Pooling=False");
        holder.Open();
        using (var begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
        }

        // Longer than the connection's five-second busy timeout, so the first attempt really fails.
        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(6.5)); // reason: the lock must outlast SQLite's real five-second busy timeout so the first attempt fails
            using var commit = holder.CreateCommand();
            commit.CommandText = "COMMIT;";
            commit.ExecuteNonQuery();
        });

        await migrator.ApplyAsync(
            DelunoDatabaseNames.Platform,
            [
                new ProbeMigration(1, "one", "CREATE TABLE probe_one (id INTEGER);"),
                new ProbeMigration(2, "two", "CREATE TABLE probe_two (id INTEGER);")
            ],
            CancellationToken.None);
        await release;

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
        Assert.Equal(2L, Convert.ToInt64(await check.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Remembering_indexer_names_before_the_update_waits_out_a_write_lock()
    {
        using var storage = TestStorage.Create();
        var path = storage.Factory.GetDatabasePath(DelunoDatabaseNames.Platform);
        await using (var setup = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform))
        {
            using var create = setup.CreateCommand();
            create.CommandText =
                """
                CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY);
                INSERT INTO schema_migrations (version) VALUES (1);
                CREATE TABLE indexer_sources (id TEXT PRIMARY KEY, name TEXT);
                INSERT INTO indexer_sources (id, name) VALUES ('a', 'NZBgeek');
                """;
            await create.ExecuteNonQueryAsync();
        }

        using var holder = new SqliteConnection($"Data Source={path};Pooling=False");
        holder.Open();
        using (var begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
        }

        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(6.5)); // reason: the lock must outlast SQLite's real five-second busy timeout so the first attempt fails
            using var commit = holder.CreateCommand();
            commit.CommandText = "COMMIT;";
            commit.ExecuteNonQuery();
        });

        await IndexerIdentity.RememberNamesBeforeRenameAsync(
            storage.Factory,
            CancellationToken.None,
            new MigrationLockRetryOptions { MaxWait = TimeSpan.FromSeconds(60), FirstPause = TimeSpan.FromMilliseconds(200), MaxPause = TimeSpan.FromSeconds(1) });
        await release;

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT old_name FROM indexer_previous_names WHERE indexer_id = 'a';";
        Assert.Equal("NZBgeek", Convert.ToString(await check.ExecuteScalarAsync()));
    }

    private sealed class ProbeMigration(int version, string name, string sql) : SqliteSqlMigration
    {
        public override int Version => version;

        public override string Name => name;

        protected override string Sql => sql;
    }
}
