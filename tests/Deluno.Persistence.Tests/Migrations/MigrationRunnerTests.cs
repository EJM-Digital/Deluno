using System.Data.Common;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Migrations;

public sealed class MigrationRunnerTests
{
    [Fact]
    public async Task ApplyAsync_applies_pending_migrations_once_and_records_history()
    {
        using var storage = TestStorage.Create();
        var migrator = new SqliteDatabaseMigrator(
            storage.Factory,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T05:00:00Z")));

        await migrator.ApplyAsync(
            DelunoDatabaseNames.Platform,
            [new CreateProbeTableMigration()],
            CancellationToken.None);

        await migrator.ApplyAsync(
            DelunoDatabaseNames.Platform,
            [new CreateProbeTableMigration()],
            CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);

        Assert.Equal(1, await ReadScalarAsync<int>(connection, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal(1, await ReadScalarAsync<int>(connection, "SELECT apply_count FROM migration_probe WHERE id = 1;"));
    }

    [Fact]
    public async Task ApplyAsync_rejects_previously_applied_migration_when_definition_changes()
    {
        using var storage = TestStorage.Create();
        var migrator = new SqliteDatabaseMigrator(
            storage.Factory,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T05:00:00Z")));

        await migrator.ApplyAsync(
            DelunoDatabaseNames.Platform,
            [new CreateProbeTableMigration()],
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            migrator.ApplyAsync(
                DelunoDatabaseNames.Platform,
                [new ChangedProbeTableMigration()],
                CancellationToken.None));

        Assert.Contains("different definition", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Schema_initializers_record_initial_migration_for_each_database()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T05:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, timeProvider);

        await new PlatformSchemaInitializer(
            storage.Factory,
            migrator,
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new MoviesSchemaInitializer(
            storage.Factory,
            migrator,
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new SeriesSchemaInitializer(
            storage.Factory,
            migrator,
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new JobsSchemaInitializer(
            storage.Factory,
            migrator,
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new CacheSchemaInitializer(
            storage.Factory,
            migrator,
            NullLogger<CacheSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        foreach (var databaseName in new[]
                 {
                     DelunoDatabaseNames.Cache
                 })
        {
            await using var connection = await storage.Factory.OpenConnectionAsync(databaseName);
            Assert.Equal(1, await ReadScalarAsync<int>(connection, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.Equal("initial_schema", await ReadScalarAsync<string>(connection, "SELECT name FROM schema_migrations WHERE version = 1;"));
        }

        await using var moviesConnection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies);
        Assert.Equal(2, await ReadScalarAsync<int>(moviesConnection, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal("initial_schema", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 1;"));
        Assert.Equal("movie_idempotency_indexes", await ReadScalarAsync<string>(moviesConnection, "SELECT name FROM schema_migrations WHERE version = 2;"));

        await using var seriesConnection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series);
        Assert.Equal(2, await ReadScalarAsync<int>(seriesConnection, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal("initial_schema", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 1;"));
        Assert.Equal("series_idempotency_indexes", await ReadScalarAsync<string>(seriesConnection, "SELECT name FROM schema_migrations WHERE version = 2;"));

        await using var platformConnection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);
        Assert.Equal(2, await ReadScalarAsync<int>(platformConnection, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal("initial_schema", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 1;"));
        Assert.Equal("user_security_stamp", await ReadScalarAsync<string>(platformConnection, "SELECT name FROM schema_migrations WHERE version = 2;"));

        await using var jobsConnection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs);
        Assert.Equal(2, await ReadScalarAsync<int>(jobsConnection, "SELECT COUNT(*) FROM schema_migrations;"));
        Assert.Equal("initial_schema", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 1;"));
        Assert.Equal("job_integrity", await ReadScalarAsync<string>(jobsConnection, "SELECT name FROM schema_migrations WHERE version = 2;"));
    }

    private static async Task<T> ReadScalarAsync<T>(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value!, typeof(T));
    }

    private sealed class CreateProbeTableMigration : SqliteSqlMigration
    {
        public override int Version => 1;

        public override string Name => "create_probe";

        protected override string Sql =>
            """
            CREATE TABLE migration_probe (
                id INTEGER PRIMARY KEY,
                apply_count INTEGER NOT NULL
            );

            INSERT INTO migration_probe (id, apply_count)
            VALUES (1, 1);
            """;
    }

    private sealed class ChangedProbeTableMigration : SqliteSqlMigration
    {
        public override int Version => 1;

        public override string Name => "create_probe";

        protected override string Sql =>
            """
            CREATE TABLE migration_probe_changed (
                id INTEGER PRIMARY KEY
            );
            """;
    }
}
