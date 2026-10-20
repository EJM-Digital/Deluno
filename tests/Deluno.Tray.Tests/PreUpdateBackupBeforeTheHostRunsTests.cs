using System.IO.Compression;
using System.Text.Json;
using Deluno.Tray;
using Microsoft.Data.Sqlite;

namespace Deluno.Tray.Tests;

/// <summary>
/// The pre-update backup before the host runs (<c>TakePreUpdateBackupAsync(null, ...)</c>) builds the backup service over the
/// saved data folder. Each test uses a real data folder with real SQLite databases and a real protection key file.
/// </summary>
public sealed class PreUpdateBackupBeforeTheHostRunsTests : IDisposable
{
    private readonly string? _originalLocalAppData = Environment.GetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA");
    private readonly string? _originalCommonAppData = Environment.GetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-preupdate-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataRoot;

    public PreUpdateBackupBeforeTheHostRunsTests()
    {
        Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA", Path.Combine(_root, "local"));
        Environment.SetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA", Path.Combine(_root, "common"));
        _dataRoot = AppSettings.RequireDataRoot();
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA", _originalLocalAppData);
        Environment.SetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA", _originalCommonAppData);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private void MakeDatabase(string name)
    {
        var path = Path.Combine(_dataRoot, name);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "create table marker (value text); insert into marker values ('" + name + "');";
            command.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task Before_the_host_runs_the_backup_holds_every_database_and_reports_each_file()
    {
        string[] databases = ["movies.db", "series.db", "platform.db"];
        foreach (var name in databases)
        {
            MakeDatabase(name);
        }

        var keyFolder = Path.Combine(_dataRoot, "protection-keys");
        Directory.CreateDirectory(keyFolder);
        File.WriteAllText(Path.Combine(keyFolder, "key-1.xml"), "<key/>");

        var reported = new List<string>();
        await PreUpdateBackupGate.TakePreUpdateBackupAsync(null, CancellationToken.None, reported.Add);

        var archivePath = Assert.Single(Directory.GetFiles(Path.Combine(_dataRoot, "backups"), "deluno-backup-*.zip"));
        using var archive = ZipFile.OpenRead(archivePath);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();
        foreach (var name in databases)
        {
            Assert.Contains($"data/{name}", names);
        }

        using var manifestStream = archive.GetEntry("deluno-backup.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        Assert.Equal("pre-update", manifest.RootElement.GetProperty("reason").GetString());
        var listed = manifest.RootElement.GetProperty("files").EnumerateArray().Select(e => e.GetString()).ToArray();
        foreach (var name in databases)
        {
            Assert.Contains(name, listed);
        }

        // Each file is reported as progress, in the "N of M: name" form the start screen shows.
        Assert.Equal(databases.Length, reported.Count);
        Assert.Equal(["1 of 3", "2 of 3", "3 of 3"], reported.Select(line => line.Split(':')[0]).ToArray());
        foreach (var name in databases)
        {
            Assert.Contains(reported, line => line.EndsWith(": " + name, StringComparison.Ordinal));
        }

        // With no backup password the keys are left out, and the manifest says so rather than hiding it.
        Assert.False(manifest.RootElement.GetProperty("keysIncluded").GetBoolean());
        Assert.DoesNotContain(names, entry => entry.Contains("protection-keys"));
    }

    [Fact]
    public async Task A_backup_taken_before_the_host_runs_leaves_the_databases_unchanged_and_readable()
    {
        MakeDatabase("movies.db");
        var before = File.ReadAllBytes(Path.Combine(_dataRoot, "movies.db"));

        await PreUpdateBackupGate.TakePreUpdateBackupAsync(null, CancellationToken.None);

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_dataRoot, "movies.db")));
        var archivePath = Assert.Single(Directory.GetFiles(Path.Combine(_dataRoot, "backups"), "deluno-backup-*.zip"));
        var copy = Path.Combine(_root, "copy.db");
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            archive.GetEntry("data/movies.db")!.ExtractToFile(copy);
        }

        using var connection = new SqliteConnection($"Data Source={copy};Pooling=False;Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "select value from marker";
        Assert.Equal("movies.db", (string?)command.ExecuteScalar());
    }
}
