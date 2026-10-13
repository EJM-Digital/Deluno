using Deluno.Infrastructure.Storage;
using Deluno.Tray;
using Microsoft.Extensions.Time.Testing;

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

    // ------------------------------------------------------------------ #1256: the backup at start is watched

    private sealed class Steps : IStartupProgress
    {
        public List<(StartupPhase Phase, string? Detail)> Entered { get; } = [];
        public void Enter(StartupPhase phase, string? detail = null) => Entered.Add((phase, detail));
    }

    [Fact]
    public async Task Start_says_that_it_is_backing_up_and_records_the_phase_before_the_backup_runs()
    {
        var progress = new Steps();
        var log = new List<string>();
        var phaseWhenBackupRan = (StartupPhase?)null;

        await PreUpdateBackupGate.InstallAtStartAsync(
            "2.0.0", _folder, _folder,
            _ => { phaseWhenBackupRan = progress.Entered.LastOrDefault().Phase; return Task.CompletedTask; },
            () => { },
            progress,
            log: log.Add);

        Assert.Equal(StartupPhase.PreUpdateBackup, phaseWhenBackupRan);
        Assert.Contains(log, line => line.Contains("backing up", StringComparison.OrdinalIgnoreCase) && line.Contains("2.0.0"));
        Assert.Contains(log, line => line.Contains("backup finished", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_backup_that_ignores_its_time_limit_still_ends_the_wait_and_blocks_the_update_with_a_note()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero));
        var applied = false;
        var neverEnds = new TaskCompletionSource();
        var log = new List<string>();

        // A backup stuck in a read that never returns and never looks at its token.
        var running = PreUpdateBackupGate.InstallAtStartAsync(
            "2.0.0", _folder, _folder,
            _ => neverEnds.Task,
            () => applied = true,
            new Steps(),
            clock,
            log.Add);

        var passed = TimeSpan.Zero;
        while (!running.IsCompleted)
        {
            Assert.True(passed < TimeSpan.FromHours(1), "the start was still waiting for the backup an hour later");
            clock.Advance(TimeSpan.FromSeconds(10));
            passed += TimeSpan.FromSeconds(10);
            await Task.Yield();
        }

        Assert.False(await running);
        Assert.False(applied);
        Assert.InRange(passed, PreUpdateBackupGate.BackupTimeout, PreUpdateBackupGate.BackupTimeout + PreUpdateBackupGate.HardLimitGrace + TimeSpan.FromSeconds(30));
        var note = BlockedUpdateNote.TryTake(_folder);
        Assert.NotNull(note);
        Assert.Contains("took too long", note!.Reason);
        Assert.False(File.Exists(Path.Combine(_folder, "update-start-attempt.txt")), "a blocked install is not an attempt: the next start tries again");
    }

    [Fact]
    public void The_start_window_for_the_backup_outlasts_the_backup_s_hard_limit()
    {
        Assert.True(new StartWindows().PreUpdateBackup > PreUpdateBackupGate.BackupTimeout + PreUpdateBackupGate.HardLimitGrace);
    }
}
