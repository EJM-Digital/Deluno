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
