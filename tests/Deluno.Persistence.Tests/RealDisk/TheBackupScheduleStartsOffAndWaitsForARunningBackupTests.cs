using Deluno.Api.Backup;
using Deluno.Infrastructure.Storage;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The backup schedule on a real data folder (coverage register, engines): a new install has scheduled backups off, with the standard
/// daily 03:00 and keep-seven choices and no next run until the person turns them on; and saving the schedule waits for a backup that
/// is running, so the settings that backup read are never put back over the new ones. Refs #1296.
/// </summary>
public sealed class TheBackupScheduleStartsOffAndWaitsForARunningBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-backup-gate-tests", Guid.NewGuid().ToString("N"));

    public TheBackupScheduleStartsOffAndWaitsForARunningBackupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private DelunoBackupService Service()
        => new(Options.Create(new StoragePathOptions { DataRoot = _root }), new FixedTimeProvider(DateTimeOffset.Parse("2026-05-14T01:00:00Z")), NullLogger<DelunoBackupService>.Instance);

    [Fact]
    public async Task A_new_install_has_scheduled_backups_off_with_the_standard_choices_and_no_next_run()
    {
        var settings = await Service().GetSettingsAsync(CancellationToken.None);

        Assert.False(settings.Enabled);
        Assert.Equal("daily", settings.Frequency);
        Assert.Equal("03:00", settings.TimeOfDay);
        Assert.Equal(7, settings.RetentionCount);
        Assert.Null(settings.NextRunUtc);
        Assert.Null(settings.LastRunUtc);
    }

    [Fact]
    public async Task Saving_the_schedule_waits_for_a_backup_that_is_running_and_then_keeps_what_the_person_chose()
    {
        File.WriteAllText(Path.Combine(_root, "platform.db"), "x");
        var service = Service();
        using var inside = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        void Hold(string _)
        {
            inside.Set();
            // reason: holds the backup open so the settings save has something to wait for; bounded so a broken test cannot hang
            Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
        }

        var backup = Task.Run(() => service.CreateBackupAsync("manual", CancellationToken.None, Hold));
        Assert.True(inside.Wait(TimeSpan.FromSeconds(30)));

        var save = service.SaveSettingsAsync(new UpdateBackupSettingsRequest(true, "weekly", "04:15", 3, null), CancellationToken.None);

        var finishedEarly = await Task.WhenAny(save, Task.Delay(500)) == save; // reason: a save that does not wait would finish at once; half a second gives it every chance to, and a save that waits is still open
        Assert.False(finishedEarly, "the settings save must wait for the running backup");

        release.Set();
        await backup;
        var saved = await save;

        Assert.True(saved.Enabled);
        Assert.Equal("weekly", saved.Frequency);
        Assert.Equal("04:15", saved.TimeOfDay);
        Assert.Equal(3, saved.RetentionCount);
        var stored = await service.GetSettingsAsync(CancellationToken.None);
        Assert.True(stored.Enabled);
        Assert.Equal(3, stored.RetentionCount);
        Assert.NotNull(stored.NextRunUtc);
    }
}
