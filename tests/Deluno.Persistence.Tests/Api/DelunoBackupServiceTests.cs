using Deluno.Api.Backup;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Api;

public sealed class DelunoBackupServiceTests
{
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
        Assert.Equal("source-platform", File.ReadAllText(targetPreexistingFile));
        Assert.Equal("target-platform-before-restore", File.ReadAllText(targetPreexistingFile + ".pre-restore"));
        Assert.Equal("source-movies", File.ReadAllText(Path.Combine(targetRoot.Path, "movies.db")));
        Assert.Equal("source-series", File.ReadAllText(Path.Combine(targetRoot.Path, "series.db")));
        Assert.Equal("""{"mode":"source"}""", File.ReadAllText(Path.Combine(targetRoot.Path, "cache", "state.json")));
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
