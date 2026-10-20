using System.IO.Compression;
using Deluno.Api.Backup;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// BKP-17: a library with a lot of history is backed up whole, on real files: the database is copied through SQLite while it is
/// being written to, the zip is valid, every row is in the copy, and the backup says which file it is on, so a long one can be told
/// from a stuck one (#1256). A 90 MB database stands in for the tens of GB a big library has: the copy is streamed, not held in memory.
/// </summary>
public sealed class BackupOfALargeLibraryTests : IDisposable
{
    private const int Rows = 45_000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-backup-large-tests", Guid.NewGuid().ToString("N"));

    public BackupOfALargeLibraryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void SeedHistory(string name)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, name), Pooling = false }.ToString());
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE search_history (id INTEGER PRIMARY KEY, title TEXT NOT NULL, detail BLOB NOT NULL);";
            create.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO search_history (title, detail) VALUES ($title, $detail);";
        var title = insert.Parameters.Add("$title", SqliteType.Text);
        var detail = insert.Parameters.Add("$detail", SqliteType.Blob);
        var random = new Random(17);
        var bytes = new byte[2000];
        for (var row = 0; row < Rows; row++)
        {
            random.NextBytes(bytes);
            title.Value = "Release " + row;
            detail.Value = bytes;
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    [Fact]
    public async Task A_database_of_ninety_megabytes_is_backed_up_whole_with_every_row_and_the_backup_names_each_file_as_it_goes()
    {
        SeedHistory("jobs.db");
        File.WriteAllText(Path.Combine(_root, "platform.db"), "not a database, a small file");
        var service = new DelunoBackupService(Options.Create(new StoragePathOptions { DataRoot = _root }), new FixedTimeProvider(DateTimeOffset.Parse("2026-10-20T01:00:00Z")), NullLogger<DelunoBackupService>.Instance);
        var progress = new List<string>();

        var item = await service.CreateBackupAsync("manual", CancellationToken.None, progress.Add);

        Assert.True(new FileInfo(Path.Combine(_root, "jobs.db")).Length > 80_000_000, "the library is a big one");
        Assert.Equal(["1 of 2: jobs.db", "2 of 2: platform.db"], progress.OrderBy(line => line, StringComparer.Ordinal).Select(line => line).ToArray());
        Assert.True(File.Exists(item.FullPath));
        var extracted = Path.Combine(_root, "extracted.db");
        using (var archive = ZipFile.OpenRead(item.FullPath))
        {
            Assert.Contains(archive.Entries, entry => entry.FullName == "data/platform.db");
            var entry = archive.GetEntry("data/jobs.db")!;
            Assert.True(entry.Length > 80_000_000);
            entry.ExtractToFile(extracted);
        }

        using var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = extracted, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        copy.Open();
        using var count = copy.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM search_history;";
        Assert.Equal(Rows, Convert.ToInt32(count.ExecuteScalar()));
        using var integrity = copy.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", integrity.ExecuteScalar());
    }
}
