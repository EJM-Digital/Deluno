using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1256: the pieces of RelaunchAfterSetup are joined up where Deluno starts, and the other ways Deluno is replaced (the
/// in-app "Restart to update", an update applied at start) still relaunch it exactly once.
/// </summary>
public sealed class RelaunchWiringTests
{
    private static string Source(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "apps", "windows-tray", "Program.cs")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine(dir!.FullName, "apps", "windows-tray", file));
    }

    [Fact]
    public void The_install_and_update_hook_arranges_the_restart_before_it_returns()
    {
        var main = Source("Program.cs");
        var fast = main.IndexOf("if (IsFastExitInstallHook(args))", StringComparison.Ordinal);
        var arrange = main.IndexOf("RelaunchAfterSetup.OnInstallOrUpdateHook()", StringComparison.Ordinal);
        var next = main.IndexOf("LogColdStart();", StringComparison.Ordinal);
        Assert.True(fast > 0 && arrange > fast && arrange < next, "the fast install/update hook must arrange the restart");
    }

    [Fact]
    public void The_tray_marks_itself_running_after_it_holds_the_single_instance_lock_and_the_service_marks_itself_too()
    {
        var main = Source("Program.cs");
        var lockTaken = main.IndexOf("DelunoTrayApplication", StringComparison.Ordinal);
        var mark = main.IndexOf("MarkRunning(RunningKind.Tray)", StringComparison.Ordinal);
        Assert.True(lockTaken > 0 && mark > lockTaken, "a copy that loses the lock must not leave a mark");
        Assert.Contains("MarkRunning(RunningKind.Service)", Source("ServiceHost.cs"));
    }

    [Fact]
    public void A_start_after_Setup_that_loses_the_lock_leaves_quietly_without_a_balloon()
    {
        var main = Source("Program.cs");
        var step = main.IndexOf("RelaunchAfterSetup.ShouldStepAside(args", StringComparison.Ordinal);
        var notify = main.IndexOf("SecondLaunchSignal.Notify()", StringComparison.Ordinal);
        Assert.True(step > 0 && step < notify, "the quiet exit must come before the 'already running' balloon");
    }

    [Fact]
    public void The_first_run_steps_and_the_first_run_icon_both_ask_whether_this_is_a_reinstall()
    {
        Assert.Contains("ExistingInstall.RunFirstRunSteps(", Source("InstallHooks.cs"));
        Assert.Contains("ExistingInstall.DetectThisMachine()", Source("FirstRunTray.cs"));
    }

    [Fact]
    public void An_in_app_update_that_has_already_started_the_new_Deluno_is_not_started_a_second_time()
    {
        // Update.exe ends the old Deluno and starts the new one itself. If the old one left its mark (it was killed rather
        // than exited), the mark names a process that is gone, but the new Deluno holds a mark of its own: here, this
        // very process stands for it, and a start that finds Deluno running starts nothing.
        var store = new MemoryStore();
        var marks = new RunningMarks(store);
        marks.Set(RunningKind.Tray, RunningMark.ThisProcess());
        var started = 0;

        var kind = RelaunchAfterSetup.Schedule(marks, Environment.ProcessPath!, mark => mark.IsRunning(), () => false, () => 1234, () => null, "x.log", _ => started++, _ => { });

        Assert.Equal(RelaunchKind.None, kind);
        Assert.Equal(0, started);
    }

    [Fact]
    public void A_tray_that_quit_in_order_cleared_its_mark_so_the_next_Setup_starts_nothing()
    {
        var marks = new RunningMarks(new MemoryStore());
        marks.Set(RunningKind.Tray, RunningMark.ThisProcess());
        marks.Clear(RunningKind.Tray, Environment.ProcessId);
        var started = 0;

        var kind = RelaunchAfterSetup.Schedule(marks, Environment.ProcessPath!, _ => false, () => false, () => 1234, () => null, "x.log", _ => started++, _ => { });

        Assert.Equal(RelaunchKind.None, kind);
        Assert.Equal(0, started);
    }

    private sealed class MemoryStore : IRunningMarkStore
    {
        private readonly Dictionary<RunningKind, RunningMark> _marks = [];
        public RunningMark? Read(RunningKind kind) => _marks.TryGetValue(kind, out var m) ? m : null;
        public void Write(RunningKind kind, RunningMark mark) => _marks[kind] = mark;
        public void Remove(RunningKind kind) => _marks.Remove(kind);
    }
}
