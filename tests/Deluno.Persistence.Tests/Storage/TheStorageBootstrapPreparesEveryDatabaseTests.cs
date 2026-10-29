using System.Data.Common;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Storage;

/// <summary>
/// Start-up prepares every database file it opens (write-ahead log, synchronous NORMAL, foreign keys on), waits out a
/// lock, and stops the host on any other failure: Deluno must not run on a database it could not prepare. A schema
/// initializer that fails marks its database failed and a later success marks it ready.
/// </summary>
public sealed class TheStorageBootstrapPreparesEveryDatabaseTests
{
    private static readonly MigrationLockRetryOptions Quick = new()
    {
        MaxWait = TimeSpan.FromSeconds(5),
        FirstPause = TimeSpan.FromMilliseconds(10),
        MaxPause = TimeSpan.FromMilliseconds(20)
    };

    /// <summary>A real database connection that remembers every command it was asked to run.</summary>
    private sealed class RecordingConnection(string connectionString, List<DbCommand> commands) : SqliteConnection(connectionString)
    {
        protected override DbCommand CreateDbCommand()
        {
            var command = base.CreateDbCommand();
            commands.Add(command);
            return command;
        }
    }

    private sealed class RecordingFactory(TestStorage storage, Func<string, int, Exception?> failure) : IDelunoDatabaseConnectionFactory
    {
        public List<DbCommand> Commands { get; } = [];

        public Dictionary<string, int> Opens { get; } = [];

        public string GetDatabasePath(string databaseName) => storage.Factory.GetDatabasePath(databaseName);

        public long GetWriteGeneration(string databaseName) => storage.Factory.GetWriteGeneration(databaseName);

        public ValueTask<DbConnection> OpenReadOnlyConnectionAsync(string databaseName, CancellationToken cancellationToken = default)
            => storage.Factory.OpenReadOnlyConnectionAsync(databaseName, cancellationToken);

        public async ValueTask<DbConnection> OpenConnectionAsync(string databaseName, CancellationToken cancellationToken = default)
        {
            Opens[databaseName] = Opens.GetValueOrDefault(databaseName) + 1;
            var error = failure(databaseName, Opens[databaseName]);
            if (error is not null)
            {
                throw error;
            }

            var connection = new RecordingConnection($"Data Source={GetDatabasePath(databaseName)}", Commands);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
    }

    private sealed class FailsOnceMigrator : IDelunoDatabaseMigrator
    {
        private int calls;

        public Task ApplyAsync(string databaseName, IReadOnlyList<IDelunoDatabaseMigration> migrations, CancellationToken cancellationToken)
            => ++calls == 1
                ? Task.FromException(new InvalidOperationException("The jobs database could not be changed."))
                : Task.CompletedTask;
    }

    private static DelunoStorageBootstrapService Service(TestStorage storage, IDelunoDatabaseConnectionFactory factory)
        => new(
            factory,
            Options.Create(new StoragePathOptions { DataRoot = storage.DataRoot }),
            NullLogger<DelunoStorageBootstrapService>.Instance,
            Quick);

    [Fact]
    public async Task Every_database_it_opens_is_put_in_write_ahead_mode_with_synchronous_normal_and_foreign_keys_on()
    {
        using var storage = TestStorage.Create();
        var factory = new RecordingFactory(storage, (_, _) => null);

        await Service(storage, factory).StartAsync(CancellationToken.None);

        var perDatabase = DelunoStorageLayout.Databases.Count;
        var sql = factory.Commands.Select(command => command.CommandText).ToList();
        Assert.Equal(perDatabase, sql.Count(text => text == "PRAGMA journal_mode = WAL;"));
        Assert.Equal(perDatabase, sql.Count(text => text == "PRAGMA synchronous = NORMAL;"));
        Assert.Equal(perDatabase, sql.Count(text => text == "PRAGMA foreign_keys = ON;"));

        // And the file on disk really is in write-ahead mode afterwards.
        foreach (var database in DelunoStorageLayout.Databases)
        {
            await using var check = new SqliteConnection($"Data Source={storage.Factory.GetDatabasePath(database.Key)};Pooling=False");
            await check.OpenAsync();
            using var command = check.CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", (string?)await command.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task A_failure_that_is_not_a_lock_stops_the_start_at_once_without_a_retry()
    {
        using var storage = TestStorage.Create();
        var factory = new RecordingFactory(storage, (_, _) => new IOException("The disk could not be written to."));

        var thrown = await Assert.ThrowsAsync<IOException>(() => Service(storage, factory).StartAsync(CancellationToken.None));

        Assert.Equal("The disk could not be written to.", thrown.Message);
        Assert.Equal(1, factory.Opens.Values.Sum());
    }

    [Fact]
    public async Task A_database_another_process_holds_locked_is_waited_for_and_the_start_then_succeeds()
    {
        using var storage = TestStorage.Create();
        var factory = new RecordingFactory(
            storage,
            (database, attempt) => attempt < 3 ? new SqliteException("database is locked", 5) : null);

        await Service(storage, factory).StartAsync(CancellationToken.None);

        Assert.All(DelunoStorageLayout.Databases, database => Assert.Equal(3, factory.Opens[database.Key]));
    }

    [Fact]
    public async Task A_schema_initializer_that_fails_marks_its_database_failed_and_stops_the_host_then_a_good_start_marks_it_ready()
    {
        var gate = new DelunoStartupGate();
        var migrator = new FailsOnceMigrator();
        using var storage = TestStorage.Create();
        var initializer = new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance, gate);

        await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.StartAsync(CancellationToken.None));

        Assert.Equal("The jobs database could not be changed.", gate.FailedDatabases[DelunoDatabaseNames.Jobs]);
        Assert.Contains(DelunoDatabaseNames.Jobs, gate.PendingDatabases);

        await initializer.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(DelunoDatabaseNames.Jobs, gate.FailedDatabases.Keys);
        Assert.DoesNotContain(DelunoDatabaseNames.Jobs, gate.PendingDatabases);
    }
}
