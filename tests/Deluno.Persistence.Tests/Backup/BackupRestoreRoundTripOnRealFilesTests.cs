using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Deluno.Api.Backup;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Backup;

/// <summary>
/// A backup taken, the data changed, the backup restored and Deluno started again, all on real database files and a real data
/// folder: the restore only stages (nothing live moves), and the start-up service applies it before it opens any database, keeps
/// the files it replaced as .pre-restore copies and comes up on the restored data.
/// </summary>
public sealed class BackupRestoreRoundTripOnRealFilesTests
{
    private static DelunoBackupService Backups(TestStorage storage)
        => new(Options.Create(new StoragePathOptions { DataRoot = storage.DataRoot }), TimeProvider.System, NullLogger<DelunoBackupService>.Instance);

    private static async Task RunAsync(TestStorage storage, string sql)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string[]> NotesAsync(TestStorage storage)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform);
        return await ReadNotesAsync(connection);
    }

    private static async Task<string[]> ReadNotesAsync(System.Data.Common.DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT text FROM notes ORDER BY id;";
        var notes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            notes.Add(reader.GetString(0));
        }

        return notes.ToArray();
    }

    private static async Task<string[]> NotesInFileAsync(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync();
        return await ReadNotesAsync(connection);
    }

    [Fact]
    public async Task A_restore_stages_then_the_restart_puts_the_backups_data_back_and_keeps_what_it_replaced()
    {
        using var storage = TestStorage.Create();
        var notes = Path.Combine(storage.DataRoot, "settings", "notes.json");
        Directory.CreateDirectory(Path.GetDirectoryName(notes)!);
        await RunAsync(storage, "CREATE TABLE notes (id INTEGER PRIMARY KEY, text TEXT NOT NULL); INSERT INTO notes (text) VALUES ('taken in the backup');");
        await File.WriteAllTextAsync(notes, "taken in the backup");
        storage.Factory.ClearPools();
        var backup = await Backups(storage).CreateBackupAsync("manual", CancellationToken.None);

        await RunAsync(storage, "INSERT INTO notes (text) VALUES ('written after the backup');");
        await File.WriteAllTextAsync(notes, "changed after the backup");
        storage.Factory.ClearPools();
        RestoreResultResponseHolder staged;
        await using (var zip = File.OpenRead(backup.FullPath))
        {
            staged = new(await Backups(storage).RestoreAsync(zip, CancellationToken.None));
        }

        Assert.True(staged.Result.Restored);
        Assert.True(StagedRestore.IsPending(storage.DataRoot));
        Assert.Equal(["taken in the backup", "written after the backup"], await NotesAsync(storage));
        Assert.Equal("changed after the backup", await File.ReadAllTextAsync(notes));

        // The restart: nothing holds the files, and the start-up service runs before any database is opened.
        storage.Factory.ClearPools();
        var bootstrap = new DelunoStorageBootstrapService(storage.Factory, Options.Create(new StoragePathOptions { DataRoot = storage.DataRoot }), NullLogger<DelunoStorageBootstrapService>.Instance);
        await bootstrap.StartAsync(CancellationToken.None);

        Assert.False(StagedRestore.IsPending(storage.DataRoot));
        Assert.Equal(["taken in the backup"], await NotesAsync(storage));
        Assert.Equal("taken in the backup", await File.ReadAllTextAsync(notes));
        Assert.Equal("changed after the backup", await File.ReadAllTextAsync(notes + ".pre-restore"));
        Assert.Equal(["taken in the backup", "written after the backup"], await NotesInFileAsync(Path.Combine(storage.DataRoot, "platform.db.pre-restore")));
    }

    private sealed record RestoreResultResponseHolder(RestoreResultResponse Result);

    private static async Task<string> NewerBackupAsync(TestStorage storage)
    {
        await RunAsync(storage, "CREATE TABLE notes (id INTEGER PRIMARY KEY, text TEXT NOT NULL); INSERT INTO notes (text) VALUES ('x');");
        storage.Factory.ClearPools();
        var backup = await Backups(storage).CreateBackupAsync("manual", CancellationToken.None);
        using (var archive = ZipFile.Open(backup.FullPath, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("deluno-backup.json")!;
            BackupManifest manifest;
            await using (var read = entry.Open())
            {
                manifest = (await JsonSerializer.DeserializeAsync<BackupManifest>(read, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
            }

            entry.Delete();
            var rewritten = archive.CreateEntry("deluno-backup.json");
            await using var write = rewritten.Open();
            await write.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest with { Version = "99.0.0" }, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        }

        return backup.FullPath;
    }

    [Fact]
    public async Task A_backup_made_by_a_newer_deluno_is_warned_about_in_the_preview()
    {
        using var storage = TestStorage.Create();
        var path = await NewerBackupAsync(storage);

        await using var zip = File.OpenRead(path);
        var preview = await Backups(storage).PreviewRestoreAsync(zip, CancellationToken.None);

        Assert.True(preview.Valid);
        Assert.True(preview.NewerThanThisVersion);
        Assert.Contains(preview.Warnings, warning => warning.Contains("newer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_backup_made_by_a_newer_deluno_is_restored_only_once_the_person_has_confirmed_it()
    {
        using var storage = TestStorage.Create();
        var path = await NewerBackupAsync(storage);

        await using (var zip = File.OpenRead(path))
        {
            var refused = await Backups(storage).RestoreAsync(zip, CancellationToken.None);
            Assert.False(refused.Restored);
            Assert.Contains("newer", refused.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(StagedRestore.IsPending(storage.DataRoot));
        }

        await using (var zip = File.OpenRead(path))
        {
            var confirmed = await Backups(storage).RestoreAsync(zip, CancellationToken.None, confirmNewerVersion: true);
            Assert.True(confirmed.Restored);
            Assert.True(StagedRestore.IsPending(storage.DataRoot));
        }
    }

    [Theory]
    [InlineData("99.0.0", "1.0.0-rc.30.33", true)]
    [InlineData("1.0.0", "1.0.0-rc.30.33", true)]
    [InlineData("1.0.0-rc.30.34", "1.0.0-rc.30.33", true)]
    [InlineData("1.0.0-rc.31", "1.0.0-rc.30.33", true)]
    [InlineData("1.0.0-rc.30.33", "1.0.0-rc.30.33", false)]
    [InlineData("1.0.0-rc.30.9", "1.0.0-rc.30.33", false)]
    [InlineData("1.0.0-rc.30", "1.0.0-rc.30.33", false)]
    [InlineData("0.9.0", "1.0.0", false)]
    [InlineData("not a version", "1.0.0", false)]
    public void A_version_is_newer_by_its_numbers_then_a_release_beats_its_candidates(string candidate, string running, bool expected)
        => Assert.Equal(expected, Deluno.Api.DelunoVersion.IsNewer(candidate, running));
}
