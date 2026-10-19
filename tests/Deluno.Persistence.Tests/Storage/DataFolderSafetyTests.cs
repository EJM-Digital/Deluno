using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Storage;

/// <summary>#1361: an unwritable data folder, a stop that leaves data in the write-ahead log, a full disk and a database a newer Deluno updated.</summary>
public sealed class DataFolderSafetyTests
{
    private sealed class ProbeMigration(int version) : SqliteSqlMigration
    {
        public override int Version => version;
        public override string Name => $"probe{version}";
        protected override string Sql => $"CREATE TABLE probe_{version} (id INTEGER PRIMARY KEY);";
    }

    [Fact]
    public void A_data_folder_that_is_really_a_file_is_told_in_plain_words_naming_the_folder()
    {
        var file = Path.Combine(Path.GetTempPath(), $"deluno-notafolder-{Guid.NewGuid():N}");
        File.WriteAllText(file, "x");
        try
        {
            var problem = DataFolderCheck.Problem(Path.Combine(file, "data"));
            Assert.NotNull(problem);
            Assert.Contains("data folder", problem);
            Assert.Contains(file, problem);
            Assert.Contains("cannot be written to", problem);
            Assert.Contains("Fix:", problem);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_writable_data_folder_has_no_problem_and_leaves_nothing_behind()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"deluno-writable-{Guid.NewGuid():N}");
        try
        {
            Assert.Null(DataFolderCheck.Problem(folder));
            Assert.Empty(Directory.GetFileSystemEntries(folder));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Stopping_folds_the_write_ahead_log_into_the_database_file()
    {
        using var storage = TestStorage.Create();
        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform))
        {
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE kept (id INTEGER PRIMARY KEY, v TEXT); INSERT INTO kept (v) VALUES ('a'),('b'),('c');";
            await create.ExecuteNonQueryAsync();
        }

        var path = storage.Factory.GetDatabasePath(DelunoDatabaseNames.Platform);
        Assert.True(new FileInfo(path + "-wal").Length > 0, "the write is in the log before the stop");

        var service = new DelunoStorageBootstrapService(
            storage.Factory, Options.Create(new StoragePathOptions { DataRoot = storage.DataRoot }), NullLogger<DelunoStorageBootstrapService>.Instance);
        await service.StopAsync(CancellationToken.None);

        Assert.False(File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length > 0, "no data is left in the log");
        var copy = path + ".copy";
        File.Copy(path, copy);
        using var plain = new SqliteConnection($"Data Source={copy};Pooling=False");
        plain.Open();
        using var count = plain.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM kept;";
        Assert.Equal(3L, count.ExecuteScalar());
    }

    [Fact]
    public async Task A_database_a_newer_version_updated_is_refused_in_plain_words_and_left_untouched()
    {
        using var storage = TestStorage.Create();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, TimeProvider.System);
        await migrator.ApplyAsync(DelunoDatabaseNames.Platform, [new ProbeMigration(1), new ProbeMigration(2)], CancellationToken.None);

        var refusal = await Assert.ThrowsAsync<NewerDatabaseException>(
            () => migrator.ApplyAsync(DelunoDatabaseNames.Platform, [new ProbeMigration(1)], CancellationToken.None));

        Assert.Contains("newer version of Deluno", refusal.Message);
        Assert.Contains("platform", refusal.Message);
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);
        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'probe_2';";
        Assert.Equal(1L, tables.ExecuteScalar());
    }

    [Fact]
    public async Task The_same_or_a_newer_program_still_opens_the_database()
    {
        using var storage = TestStorage.Create();
        var migrator = new SqliteDatabaseMigrator(storage.Factory, TimeProvider.System);
        await migrator.ApplyAsync(DelunoDatabaseNames.Platform, [new ProbeMigration(1)], CancellationToken.None);
        await migrator.ApplyAsync(DelunoDatabaseNames.Platform, [new ProbeMigration(1)], CancellationToken.None);
        await migrator.ApplyAsync(DelunoDatabaseNames.Platform, [new ProbeMigration(1), new ProbeMigration(2)], CancellationToken.None);
    }

    [Theory]
    [InlineData(13, true)]   // SQLITE_FULL
    [InlineData(5, false)]   // SQLITE_BUSY
    public void A_full_sqlite_write_is_recognised_and_a_busy_one_is_not(int code, bool full)
    {
        var exception = new SqliteException(full ? "SQLite Error 13: 'database or disk is full'." : "SQLite Error 5: 'database is locked'.", code);
        Assert.Equal(full, DiskFull.Caused(exception));
        Assert.Equal(full, DiskFull.Caused(new InvalidOperationException("wrapped", exception)));
    }

    [Fact]
    public void A_file_write_that_found_no_space_is_recognised()
    {
        Assert.True(DiskFull.Caused(new IOException("No space left on device : '/data/x'")));
        Assert.False(DiskFull.Caused(new IOException("The file is in use")));
        Assert.False(DiskFull.Caused(null));
    }
}
