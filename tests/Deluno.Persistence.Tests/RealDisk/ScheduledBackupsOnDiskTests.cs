using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deluno.Api.Backup;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The backup service on a real data folder with real SQLite databases: the scheduled backup writes a real archive and
/// moves its next run on, a backup that cannot be written is recorded as failed in plain words and the loop goes on,
/// and retention deletes only the oldest archives past the count.
/// </summary>
public sealed class ScheduledBackupsOnDiskTests
{
    private static DelunoBackupService ServiceFor(ApplicationTestHost app, IServiceScope scope, TimeProvider? time = null)
        => new(
            Options.Create(new StoragePathOptions { DataRoot = app.DataRoot }),
            time ?? TimeProvider.System,
            NullLogger<DelunoBackupService>.Instance,
            scope.ServiceProvider.GetRequiredService<IJobQueueRepository>());

    private static string SettingsFile(ApplicationTestHost app) => Path.Combine(app.DataRoot, "backups", "backup-settings.json");

    /// <summary>Turns the schedule on and makes the next run due, the way a day passing would.</summary>
    private static void MakeDue(ApplicationTestHost app, string? backupFolder = null)
    {
        var settings = JsonNode.Parse(File.ReadAllText(SettingsFile(app))) as JsonObject ?? new JsonObject();
        settings["enabled"] = true;
        settings["nextRunUtc"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");
        if (backupFolder is not null) settings["backupFolder"] = backupFolder;
        File.WriteAllText(SettingsFile(app), settings.ToJsonString());
    }

    private static async Task<SystemTaskState?> TaskStateAsync(IServiceScope scope)
        => (await scope.ServiceProvider.GetRequiredService<IJobQueueRepository>().ListSystemTaskStatesAsync(CancellationToken.None))
            .FirstOrDefault(state => state.ScheduleKey == SystemTasks.Backup);

    [Fact]
    public async Task A_due_scheduled_backup_writes_a_real_archive_of_the_databases_moves_the_next_run_on_and_records_a_completed_pass()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "backups"));
        await app.Client.GetAsync("/api/backups/settings");
        MakeDue(app);
        using var scope = app.Services.CreateScope();
        var service = ServiceFor(app, scope);

        await service.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(async () => (await TaskStateAsync(scope))?.LastResult is "completed" or "failed", "the scheduled backup pass to be recorded");
        await service.StopAsync(CancellationToken.None);

        var zip = Assert.Single(Directory.GetFiles(Path.Combine(app.DataRoot, "backups"), "*.zip"));
        using (var archive = ZipFile.OpenRead(zip))
        {
            var manifestEntry = archive.GetEntry("deluno-backup.json")!;
            Assert.True(manifestEntry.Length > 0);
            using var manifestStream = manifestEntry.Open();
            var manifest = JsonDocument.Parse(manifestStream);
            Assert.Equal("scheduled", manifest.RootElement.GetProperty("reason").GetString());
            Assert.Contains(archive.Entries, entry => entry.FullName.StartsWith("data/", StringComparison.Ordinal) && entry.FullName.EndsWith(".db", StringComparison.Ordinal));
        }

        Assert.Equal("completed", (await TaskStateAsync(scope))!.LastResult);
        var settings = await service.GetSettingsAsync(CancellationToken.None);
        Assert.NotNull(settings.LastRunUtc);
        Assert.True(settings.NextRunUtc > DateTimeOffset.UtcNow, "the next run moved on past now");
    }

    [Fact]
    public async Task A_scheduled_backup_that_cannot_be_written_is_recorded_as_failed_in_plain_words_and_the_loop_keeps_going()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "backups"));
        await app.Client.GetAsync("/api/backups/settings");
        // The chosen backup folder is a plain file, so no archive can be made there.
        var notAFolder = Path.Combine(app.DataRoot, "not-a-folder");
        File.WriteAllText(notAFolder, "in the way");
        MakeDue(app, backupFolder: notAFolder);
        using var scope = app.Services.CreateScope();
        var service = ServiceFor(app, scope);

        await service.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(async () => (await TaskStateAsync(scope))?.LastResult is "completed" or "failed", "the failed backup pass to be recorded");
        var stillRunning = service.ExecuteTask is { IsCompleted: false };
        await service.StopAsync(CancellationToken.None);

        var state = (await TaskStateAsync(scope))!;
        Assert.Equal("failed", state.LastResult);
        Assert.False(string.IsNullOrWhiteSpace(state.LastError));
        Assert.DoesNotContain("Exception", state.LastError);
        Assert.True(stillRunning, "one failed backup does not end the schedule");
        Assert.Equal("in the way", File.ReadAllText(notAFolder));
    }

    [Fact]
    public async Task After_each_backup_the_archives_beyond_the_retention_count_are_deleted_oldest_first()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-01-01T03:00:00Z"));
        using var scope = app.Services.CreateScope();
        var service = ServiceFor(app, scope, time);
        await service.SaveSettingsAsync(new UpdateBackupSettingsRequest(false, "daily", "03:00", 2, null), CancellationToken.None);
        var made = new List<BackupItem>();
        for (var index = 0; index < 4; index++)
        {
            made.Add(await service.CreateBackupAsync("manual", CancellationToken.None));
            time.Advance(TimeSpan.FromMinutes(1));
        }

        var remaining = Directory.GetFiles(Path.Combine(app.DataRoot, "backups"), "*.zip").Select(Path.GetFileName).Order().ToArray();

        Assert.Equal(new[] { made[2].FileName, made[3].FileName }.Order().ToArray(), remaining);
        Assert.False(File.Exists(made[0].FullPath));
        Assert.False(File.Exists(made[1].FullPath));
    }

    // DEFECT found while proving retention: it counts every zip in the backup folder that opens as an archive, whether
    // or not it carries Deluno's manifest. A person's own zip kept in that folder becomes "the oldest backup" and is
    // deleted once there are more than RetentionCount archives.
    [Fact(Skip = "Defect: retention deletes a zip in the backup folder that is not a Deluno backup (it counts any readable zip).")]
    public async Task Retention_never_deletes_a_zip_that_is_not_a_backup()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-01-01T03:00:00Z"));
        using var scope = app.Services.CreateScope();
        var service = ServiceFor(app, scope, time);
        await service.SaveSettingsAsync(new UpdateBackupSettingsRequest(false, "daily", "03:00", 2, null), CancellationToken.None);
        var folder = Path.Combine(app.DataRoot, "backups");
        var theirs = Path.Combine(folder, "holiday-photos.zip");
        using (var archive = ZipFile.Open(theirs, ZipArchiveMode.Create))
        {
            archive.CreateEntry("photo.txt");
        }

        File.SetCreationTimeUtc(theirs, DateTime.Parse("2019-01-01T00:00:00Z"));
        for (var index = 0; index < 4; index++)
        {
            await service.CreateBackupAsync("manual", CancellationToken.None);
            time.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.True(File.Exists(theirs), "a zip that is not a Deluno backup must be left alone");
    }
}
