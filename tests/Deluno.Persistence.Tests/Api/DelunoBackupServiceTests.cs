using Deluno.Api.Backup;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Api;

public sealed class DelunoBackupServiceTests
{
    [Fact]
    public async Task SaveSettings_persists_retention_above_the_old_ceiling()
    {
        using var storage = TempDataRoot.Create();
        var service = CreateService(storage.Path, "2026-05-14T01:00:00Z");

        var saved = await service.SaveSettingsAsync(
            new UpdateBackupSettingsRequest(
                Enabled: false,
                Frequency: "daily",
                TimeOfDay: "02:00",
                RetentionCount: 500,
                BackupFolder: null),
            CancellationToken.None);

        var loaded = await service.GetSettingsAsync(CancellationToken.None);

        Assert.Equal(500, saved.RetentionCount);
        Assert.Equal(500, loaded.RetentionCount);
    }

    /// <summary>
    /// The default backup folder follows the data root, and is never frozen.
    ///
    /// <para>It used to be materialised into the settings file the first time
    /// they were written, which turned "wherever the data root is" into a fixed
    /// absolute path. A data directory then carried that path with it: the
    /// physical rig inherited
    /// <c>C:\Projects\Deluno\src\Deluno.Host\dataackups</c> from a
    /// developer's checkout and created it on a server, while the operator's
    /// own data folder stayed empty. Backups were real, and nowhere anybody
    /// would look for them.</para>
    /// </summary>
    [Fact]
    public async Task An_unchosen_backup_folder_follows_the_data_root_rather_than_being_frozen()
    {
        using var first = TempDataRoot.Create();
        var service = CreateService(first.Path, "2026-09-16T05:00:00Z");

        // Nothing chosen: the settings file must not record a folder.
        await service.GetSettingsAsync(CancellationToken.None);
        var settingsFile = Path.Combine(first.Path, "backups", "backup-settings.json");
        Assert.True(File.Exists(settingsFile));
        Assert.DoesNotContain("\"backupFolder\": \"" + first.Path.Replace("\\", "\\\\"), File.ReadAllText(settingsFile), StringComparison.Ordinal);

        var created = await service.CreateBackupAsync("manual", CancellationToken.None);
        Assert.StartsWith(Path.Combine(first.Path, "backups"), created.FullPath, StringComparison.OrdinalIgnoreCase);

        // The same settings, carried to a different data root, follow it.
        using var second = TempDataRoot.Create();
        Directory.CreateDirectory(Path.Combine(second.Path, "backups"));
        File.Copy(settingsFile, Path.Combine(second.Path, "backups", "backup-settings.json"), overwrite: true);

        var moved = CreateService(second.Path, "2026-09-16T06:00:00Z");
        var movedBackup = await moved.CreateBackupAsync("manual", CancellationToken.None);

        Assert.StartsWith(Path.Combine(second.Path, "backups"), movedBackup.FullPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(first.Path, movedBackup.FullPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A folder the owner actually chose is still honoured, and still moves
    /// with them: the fix must not quietly ignore an explicit setting.
    /// </summary>
    [Fact]
    public async Task A_chosen_backup_folder_is_kept()
    {
        using var storage = TempDataRoot.Create();
        using var chosen = TempDataRoot.Create();
        var service = CreateService(storage.Path, "2026-09-16T05:00:00Z");

        await service.SaveSettingsAsync(
            new UpdateBackupSettingsRequest(false, "daily", "02:00", 7, chosen.Path),
            CancellationToken.None);

        var created = await service.CreateBackupAsync("manual", CancellationToken.None);

        Assert.StartsWith(chosen.Path, created.FullPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestoreAsync_restores_backup_into_second_machine_profile_and_keeps_pre_restore_copy()
    {
        using var sourceRoot = TempDataRoot.Create();
        using var targetRoot = TempDataRoot.Create();

        var sourceDataFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["platform.db"] = "source-platform",
            ["movies.db"] = "source-movies",
            ["series.db"] = "source-series",
            [Path.Combine("cache", "state.json")] = """{"mode":"source"}"""
        };
        SeedDataRoot(sourceRoot.Path, sourceDataFiles);

        var sourceService = CreateService(sourceRoot.Path, "2026-05-14T01:00:00Z");
        var backup = await sourceService.CreateBackupAsync("disaster-recovery-drill", CancellationToken.None);

        var targetPreexistingFile = Path.Combine(targetRoot.Path, "platform.db");
        SeedDataRoot(targetRoot.Path, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["platform.db"] = "target-platform-before-restore"
        });

        var targetService = CreateService(targetRoot.Path, "2026-05-14T02:00:00Z");
        await using var backupPreviewStream = File.OpenRead(backup.FullPath);
        var preview = await targetService.PreviewRestoreAsync(backupPreviewStream, CancellationToken.None);
        Assert.True(preview.Valid);

        await using var backupRestoreStream = File.OpenRead(backup.FullPath);
        var restored = await targetService.RestoreAsync(backupRestoreStream, CancellationToken.None);

        Assert.True(restored.Restored);
        Assert.True(Directory.Exists(restored.RestoreFolder));
        Assert.Contains("platform.db", restored.RestoredFiles, StringComparer.OrdinalIgnoreCase);

        // Staged, not applied. Deluno holds every database open while it runs,
        // so writing straight over them fails on the first file - which is what
        // it used to do, and why a restore returned a 500 and changed nothing.
        Assert.Equal("target-platform-before-restore", File.ReadAllText(targetPreexistingFile));
        Assert.True(StagedRestore.IsPending(targetRoot.Path));

        // What a restart does, and the only moment nothing is holding the files.
        StagedRestore.ApplyPending(targetRoot.Path);

        Assert.Equal("source-platform", File.ReadAllText(targetPreexistingFile));
        Assert.Equal("target-platform-before-restore", File.ReadAllText(targetPreexistingFile + ".pre-restore"));
        Assert.Equal("source-movies", File.ReadAllText(Path.Combine(targetRoot.Path, "movies.db")));
        Assert.Equal("source-series", File.ReadAllText(Path.Combine(targetRoot.Path, "series.db")));
        Assert.Equal("""{"mode":"source"}""", File.ReadAllText(Path.Combine(targetRoot.Path, "cache", "state.json")));
    }

    /// <summary>
    /// A file whose timestamp a zip cannot hold is still backed up.
    ///
    /// <para><b>This is a real failure that spent the day pretending to be a
    /// flaky test.</b> <c>Deleting_a_backup_removes_the_archive_from_disk</c>
    /// failed intermittently on full parallel runs and passed every time in
    /// isolation, which reads like test contention. It was not. Under load the
    /// backup itself was throwing:</para>
    ///
    /// <code>
    /// System.ArgumentOutOfRangeException : The DateTimeOffset specified cannot
    /// be converted into a Zip file timestamp. (Parameter 'value')
    ///    at ZipArchiveEntry.set_LastWriteTime(DateTimeOffset)
    ///    at DelunoBackupService.CreateBackupAsync
    /// </code>
    ///
    /// <para>Zip stores MS-DOS timestamps, which begin in 1980. The value that
    /// broke it is the one <see cref="File.GetLastWriteTime"/> returns for a
    /// file that is <i>not there</i>: 1601-01-01, returned rather than thrown.
    /// The backup lists its files once and then archives them one by one, and a
    /// <c>-wal</c> belongs to a database still being served — SQLite deletes it
    /// the moment the last connection to that database closes, which can happen
    /// between the two. Under load that window is wide enough to hit.</para>
    ///
    /// <para>1601 is used here directly because it is the exact value observed,
    /// and it makes the test deterministic instead of a race. A genuinely old
    /// file — restored from an archive, or written while the clock was wrong —
    /// reaches the same code by a different road, and losing its contents over
    /// its modification date would be a poor trade.</para>
    ///
    /// <para>On a live install this is a backup that fails at random, which is
    /// the worst possible behaviour for the one feature whose job is to be
    /// there when everything else was not.</para>
    /// </summary>
    [Theory]
    // What a file that has been deleted reports.
    [InlineData(1601, 1, 1)]
    // And a real timestamp from before zip's floor.
    [InlineData(1975, 6, 1)]
    public async Task A_timestamp_a_zip_cannot_hold_does_not_lose_the_backup(int year, int month, int day)
    {
        using var storage = TempDataRoot.Create();
        SeedDataRoot(storage.Path, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["platform.db"] = "platform",
            ["movies.db"] = "movies"
        });

        File.SetLastWriteTime(
            Path.Combine(storage.Path, "platform.db"),
            new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Local));

        var service = CreateService(storage.Path, "2026-09-04T02:00:00Z");

        var backup = await service.CreateBackupAsync("manual", CancellationToken.None);

        using var archive = System.IO.Compression.ZipFile.OpenRead(
            Path.Combine(storage.Path, "backups", $"{backup.Id}.zip"));
        var entry = archive.GetEntry("data/platform.db");
        Assert.NotNull(entry);
        Assert.True(entry!.LastWriteTime.Year >= 1980, "a zip can only hold 1980 onwards");
        // And the file it could not date is still the file it was.
        using var reader = new StreamReader(entry.Open());
        Assert.Equal("platform", await reader.ReadToEndAsync());
        Assert.NotNull(archive.GetEntry("data/movies.db"));
    }

    /// <summary>
    /// One unreadable file is not a reason to have no backup at all.
    ///
    /// <para>The other half of the same defect: the file list is taken once and
    /// then each file is opened. Anything that has gone, or that something else
    /// holds, used to abort the whole run. What comes back should be a backup
    /// missing one file, not an exception.</para>
    /// </summary>
    [Fact]
    public async Task A_file_it_cannot_read_is_left_out_rather_than_taking_the_backup_with_it()
    {
        using var storage = TempDataRoot.Create();
        SeedDataRoot(storage.Path, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["platform.db"] = "platform",
            ["locked.db"] = "held by someone else"
        });

        var service = CreateService(storage.Path, "2026-09-04T02:00:00Z");

        // Held exclusively for the length of the backup, which is what a file
        // another process is mid-write on looks like.
        using (new FileStream(
            Path.Combine(storage.Path, "locked.db"),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            var backup = await service.CreateBackupAsync("manual", CancellationToken.None);

            using var archive = System.IO.Compression.ZipFile.OpenRead(
                Path.Combine(storage.Path, "backups", $"{backup.Id}.zip"));
            Assert.NotNull(archive.GetEntry("data/platform.db"));
            Assert.Null(archive.GetEntry("data/locked.db"));

            // The manifest is a record of what went in, not of what was hoped
            // for, so it must not promise a file the archive does not hold.
            using var manifestStream = archive.GetEntry("deluno-backup.json")!.Open();
            var manifest = await System.Text.Json.JsonSerializer.DeserializeAsync<BackupManifest>(
                manifestStream,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            Assert.NotNull(manifest);
            Assert.DoesNotContain(manifest!.Files, file => file.Contains("locked.db", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(manifest.Files, file => file.Contains("platform.db", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// The application log lives under the data root, and a backup is of the
    /// library's state, not of two weeks of log lines - which a restore would
    /// otherwise write back over the log that explains why it was needed.
    /// </summary>
    [Fact]
    public async Task The_application_log_is_not_part_of_a_backup()
    {
        using var storage = TempDataRoot.Create();
        SeedDataRoot(storage.Path, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["platform.db"] = "platform",
            [Path.Combine("logs", "deluno-20260915.jsonl")] = "{\"message\":\"imported\"}"
        });

        var backup = await CreateService(storage.Path, "2026-09-15T02:00:00Z").CreateBackupAsync("manual", CancellationToken.None);

        using var archive = System.IO.Compression.ZipFile.OpenRead(Path.Combine(storage.Path, "backups", $"{backup.Id}.zip"));
        Assert.NotNull(archive.GetEntry("data/platform.db"));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("logs", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A backup taken while Deluno is writing is a consistent database, and no
    /// write-ahead log or its index rides along in the zip (#1084).
    /// </summary>
    [Fact]
    public async Task A_backup_taken_during_writes_restores_to_databases_that_pass_integrity_check()
    {
        using var storage = TempDataRoot.Create();
        var dbPath = Path.Combine(storage.Path, "movies.db");
        var connectionString = $"Data Source={dbPath};Pooling=False";
        using (var setup = new Microsoft.Data.Sqlite.SqliteConnection(connectionString))
        {
            setup.Open();
            Exec(setup, "PRAGMA journal_mode = WAL;");
            Exec(setup, "CREATE TABLE t (id INTEGER PRIMARY KEY, a TEXT, b TEXT); CREATE INDEX ix ON t(a);");
        }

        using var stop = new CancellationTokenSource();
        var rowsWritten = 0;
        var writer = Task.Run(() =>
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString + ";Default Timeout=30");
            connection.Open();
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                Exec(connection, $"INSERT INTO t (a, b) VALUES ('a{i}', '{new string('x', 200)}'); UPDATE t SET b = 'u{i}' WHERE id % 7 = 0;");
                i++;
                Volatile.Write(ref rowsWritten, i);
            }
        });

        // Let the writer get going, so the -wal is live.
        await Wait.UntilAsync(() => Volatile.Read(ref rowsWritten) >= 50, "the writer to have written 50 rows");
        var service = CreateService(storage.Path, "2026-09-30T02:00:00Z");
        BackupItem backup;
        try
        {
            backup = await service.CreateBackupAsync("manual", CancellationToken.None);
        }
        finally
        {
            stop.Cancel();
            await writer;
        }

        var zipPath = Path.Combine(storage.Path, "backups", $"{backup.Id}.zip");
        using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath))
        {
            Assert.NotNull(archive.GetEntry("data/movies.db"));
            Assert.DoesNotContain(archive.Entries, e =>
                e.FullName.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
                || e.FullName.EndsWith("-shm", StringComparison.OrdinalIgnoreCase));
        }

        // Nothing is left behind in the data root.
        Assert.False(Directory.Exists(Path.Combine(storage.Path, "backup-snapshots")));

        using var target = TempDataRoot.Create();
        var restoreService = CreateService(target.Path, "2026-09-30T03:00:00Z");
        await using (var zip = File.OpenRead(zipPath))
        {
            var result = await restoreService.RestoreAsync(zip, CancellationToken.None);
            Assert.True(result.Restored);
        }

        StagedRestore.ApplyPending(target.Path);
        using var restored = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(target.Path, "movies.db")};Pooling=False");
        restored.Open();
        using var command = restored.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", (string?)command.ExecuteScalar());
    }

    /// <summary>
    /// Backups made by older versions carry a -wal beside the database; they
    /// must still be accepted by restore.
    /// </summary>
    [Fact]
    public async Task An_older_backup_that_holds_a_wal_file_still_restores()
    {
        using var storage = TempDataRoot.Create();
        var zipPath = Path.Combine(storage.Path, "old.zip");
        var dbBytes = MakeDatabaseBytes(storage.Path);
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var s = zip.CreateEntry("data/movies.db").Open()) { s.Write(dbBytes); }
            using (var s = zip.CreateEntry("data/movies.db-wal").Open()) { s.Write(new byte[32]); }
            using (var s = zip.CreateEntry("deluno-backup.json").Open()) { s.Write("{\"app\":\"Deluno\",\"version\":\"1\",\"createdUtc\":\"2026-01-01T00:00:00Z\",\"reason\":\"manual\",\"files\":[\"movies.db\",\"movies.db-wal\"]}"u8); }
        }

        using var target = TempDataRoot.Create();
        var service = CreateService(target.Path, "2026-09-30T03:00:00Z");
        await using var stream = File.OpenRead(zipPath);
        var result = await service.RestoreAsync(stream, CancellationToken.None);
        Assert.True(result.Restored);
        StagedRestore.ApplyPending(target.Path);

        using var restored = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(target.Path, "movies.db")};Pooling=False");
        restored.Open();
        using var command = restored.CreateCommand();
        command.CommandText = "SELECT count(*) FROM t;";
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }

    private static byte[] MakeDatabaseBytes(string folder)
    {
        var path = Path.Combine(folder, "seed.db");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            Exec(connection, "CREATE TABLE t (id INTEGER PRIMARY KEY); INSERT INTO t DEFAULT VALUES;");
        }

        return File.ReadAllBytes(path);
    }

    private static void Exec(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>#1256: a backup taken while Deluno starts says which file it is on, before it copies it.</summary>
    [Fact]
    public async Task A_backup_says_which_file_it_is_about_to_copy_before_each_one()
    {
        using var storage = TempDataRoot.Create();
        SeedDataRoot(storage.Path, new Dictionary<string, string>
        {
            ["a-settings.json"] = "{}",
            ["b-keys.json"] = "{}",
            ["c-notes.txt"] = "x"
        });
        var service = CreateService(storage.Path, "2026-10-13T01:00:00Z");
        var steps = new List<string>();

        await service.CreateBackupAsync("pre-update", CancellationToken.None, steps.Add);

        Assert.Equal(3, steps.Count);
        Assert.StartsWith("1 of 3: ", steps[0]);
        Assert.StartsWith("3 of 3: ", steps[2]);
        Assert.All(steps, step => Assert.Contains(" of 3: ", step));
    }

    [Fact]
    public async Task A_backup_without_a_step_callback_behaves_as_before()
    {
        using var storage = TempDataRoot.Create();
        SeedDataRoot(storage.Path, new Dictionary<string, string> { ["a-settings.json"] = "{}" });
        var service = CreateService(storage.Path, "2026-10-13T01:00:00Z");

        var item = await service.CreateBackupAsync("manual", CancellationToken.None);

        Assert.True(File.Exists(item.FullPath));
    }

    private static DelunoBackupService CreateService(string dataRoot, string utcNowIso)
    {
        return new DelunoBackupService(
            Options.Create(new StoragePathOptions { DataRoot = dataRoot }),
            new FixedTimeProvider(DateTimeOffset.Parse(utcNowIso)),
            NullLogger<DelunoBackupService>.Instance);
    }

    private static void SeedDataRoot(string dataRoot, IReadOnlyDictionary<string, string> files)
    {
        foreach (var (relativePath, contents) in files)
        {
            var fullPath = Path.Combine(dataRoot, relativePath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, contents);
        }
    }

    private sealed class TempDataRoot : IDisposable
    {
        private TempDataRoot(string path)
        {
            Path = path;
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public static TempDataRoot Create()
            => new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deluno-backup-tests", Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
