using System.Diagnostics;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1451 (golden VM, update.in-app, rc.30.37): quitting while an update installed started Deluno again, because the hook
/// read the jobs database heartbeat, always fresh after a Quit. The hook now decides from the record of how Deluno ended.
/// </summary>
public sealed class TheInstallHookStartsDelunoOnlyAfterARestartTests : IDisposable
{
    private const string Exe = @"C:\Users\Example\AppData\Local\Deluno\current\Deluno.exe";
    private readonly List<ProcessStartInfo> _started = [];
    private readonly List<string> _log = [];
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-exit-" + Guid.NewGuid().ToString("N"));

    public TheInstallHookStartsDelunoOnlyAfterARestartTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* temp */ }
    }

    private sealed class Store : IRunningMarkStore
    {
        public RunningMark? Mark;
        public RunningMark? Read(RunningKind kind) => kind == RunningKind.Tray ? Mark : null;
        public void Write(RunningKind kind, RunningMark mark) => Mark = mark;
        public void Remove(RunningKind kind) => Mark = null;
    }

    // The jobs database heartbeat is fresh after a Quit; the hook no longer looks at it.
    private RelaunchKind Hook(Store store, ExitReason? reason)
        => RelaunchAfterSetup.Schedule(new RunningMarks(store), Exe, _ => false, () => false, () => 7624,
            "x.log", _started.Add, _log.Add, () => reason);

    [Fact]
    public void After_Quit_with_a_fresh_heartbeat_Deluno_stays_closed()
    {
        Assert.Equal(RelaunchKind.None, Hook(new Store(), ExitReason.Quit));
        Assert.Empty(_started);
        Assert.Contains(_log, l => l.Contains("quit") && l.Contains("left stopped"));
    }

    [Fact]
    public void After_Restart_to_update_Deluno_starts_again()
    {
        Assert.Equal(RelaunchKind.Tray, Hook(new Store(), ExitReason.Restart));
        Assert.Single(_started);
    }

    [Fact]
    public void After_a_crash_there_is_no_quit_record_so_Deluno_comes_back()
    {
        var crashed = new Store { Mark = new RunningMark(1, 2, Exe) };
        Assert.Equal(RelaunchKind.Tray, Hook(crashed, reason: null));
        Assert.Single(_started);
    }

    [Fact]
    public void An_update_applied_while_Deluno_keeps_running_leaves_a_mark_and_starts_it_again()
    {
        var running = new Store { Mark = new RunningMark(1, 2, Exe) };
        // A stale quit record does not outrank a mark: Setup ended a Deluno that was running.
        Assert.Equal(RelaunchKind.Tray, Hook(running, ExitReason.Quit));
    }

    [Fact]
    public void An_older_build_that_restarts_to_update_leaves_no_record_and_the_hook_starts_nothing_because_Velopack_restarts_it()
    {
        // Old to new: the older build writes no record and has cleared its mark on its orderly exit.
        Assert.Equal(RelaunchKind.None, Hook(new Store(), reason: null));
        Assert.Empty(_started);

        // Velopack's ApplyUpdatesAndRestart relaunches the app itself (Update.exe --restart), so every restart path uses it.
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Deluno.slnx"))) root = Path.GetDirectoryName(root);
        var source = File.ReadAllText(Path.Combine(root!, "apps", "windows-tray", "VelopackUpdateOrchestrator.cs"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(source, @"ApplyUpdatesAndRestart\(").Count);
    }

    [Fact]
    public void The_record_is_written_read_and_cleared()
    {
        var path = Path.Combine(_folder, "reason.txt");
        Assert.Null(ExitReasonNote.Read(path));
        ExitReasonNote.Write(ExitReason.Quit, path);
        Assert.Equal(ExitReason.Quit, ExitReasonNote.Read(path));
        ExitReasonNote.Write(ExitReason.Restart, path);
        Assert.Equal(ExitReason.Restart, ExitReasonNote.Read(path));
        ExitReasonNote.Clear(path);
        Assert.Null(ExitReasonNote.Read(path));
    }
}
