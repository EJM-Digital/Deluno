using Deluno.Tray;

namespace Deluno.Tray.Tests;

public sealed class PreUpdateBackupGateTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-update-gate-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* temp */ }
    }

    [Fact]
    public async Task Quit_takes_the_backup_before_the_install_is_queued()
    {
        var order = new List<string>();

        var installed = await PreUpdateBackupGate.InstallOnQuitAsync(
            "2.0.0", _folder,
            _ => { order.Add("backup"); return Task.CompletedTask; },
            () => order.Add("install"));

        Assert.True(installed);
        Assert.Equal(["backup", "install"], order);
        Assert.Null(BlockedUpdateNote.TryTake(_folder));
    }

    [Fact]
    public async Task Quit_does_not_install_when_the_backup_fails_and_leaves_a_note()
    {
        var queued = false;

        var installed = await PreUpdateBackupGate.InstallOnQuitAsync(
            "2.0.0", _folder,
            _ => throw new IOException("the backup folder is full"),
            () => queued = true);

        Assert.False(installed);
        Assert.False(queued);
        var note = BlockedUpdateNote.TryTake(_folder);
        Assert.NotNull(note);
        Assert.Equal(
            "The update to 2.0.0 wasn't installed because Deluno couldn't back up its data first: the backup folder is full",
            note!.Message);
        Assert.Null(BlockedUpdateNote.TryTake(_folder));
    }

    [Fact]
    public async Task Start_takes_the_backup_before_the_install_and_marks_the_attempt()
    {
        var order = new List<string>();

        var restarting = await PreUpdateBackupGate.InstallAtStartAsync(
            "2.0.0", _folder, _folder,
            _ => { order.Add("backup"); return Task.CompletedTask; },
            () => order.Add("install"));

        Assert.True(restarting);
        Assert.Equal(["backup", "install"], order);
        Assert.True(File.Exists(Path.Combine(_folder, "update-start-attempt.txt")));

        // The same version is not tried twice, and takes no second backup.
        var again = await PreUpdateBackupGate.InstallAtStartAsync(
            "2.0.0", _folder, _folder,
            _ => { order.Add("backup2"); return Task.CompletedTask; },
            () => order.Add("install2"));
        Assert.False(again);
        Assert.Equal(["backup", "install"], order);
    }

    [Fact]
    public async Task Start_does_not_install_when_the_backup_fails_and_tries_again_next_start()
    {
        var installs = 0;

        var restarting = await PreUpdateBackupGate.InstallAtStartAsync(
            "2.0.0", _folder, _folder,
            _ => throw new InvalidOperationException("the data folder is locked"),
            () => installs++);

        Assert.False(restarting);
        Assert.Equal(0, installs);
        Assert.False(File.Exists(Path.Combine(_folder, "update-start-attempt.txt")));
        Assert.Contains("data folder is locked", BlockedUpdateNote.TryTake(_folder)!.Message);

        var next = await PreUpdateBackupGate.InstallAtStartAsync(
            "2.0.0", _folder, _folder, _ => Task.CompletedTask, () => installs++);
        Assert.True(next);
        Assert.Equal(1, installs);
    }
}
