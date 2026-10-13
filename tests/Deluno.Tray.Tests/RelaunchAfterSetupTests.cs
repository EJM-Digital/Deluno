using System.Diagnostics;
using Deluno.Tray;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1256, the rc.30.27 golden-path run (update.over-running-previous): Setup.exe --silent over a running Deluno exited 0
/// and Deluno was never started again, because Velopack ran the install hook (--veloapp-install), the hook only logged,
/// and a silent Setup does not start the app. Deluno must come back on its own once Setup has exited, unless Setup starts
/// it, in tray mode and in service mode, and never twice.
/// </summary>
public sealed class RelaunchAfterSetupTests : IDisposable
{
    private const string Exe = @"C:\Users\Example\AppData\Local\Deluno\current\Deluno.exe";
    private const int SetupId = 7624;
    private readonly FakeStore _store = new();
    private readonly List<ProcessStartInfo> _started = [];
    private readonly List<string> _log = [];
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-relaunch-" + Guid.NewGuid().ToString("N"));

    public RelaunchAfterSetupTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* temp */ }
    }

    private sealed class FakeStore : IRunningMarkStore
    {
        private readonly Dictionary<RunningKind, RunningMark> _marks = [];
        public RunningMark? Read(RunningKind kind) => _marks.TryGetValue(kind, out var m) ? m : null;
        public void Write(RunningKind kind, RunningMark mark) => _marks[kind] = mark;
        public void Remove(RunningKind kind) => _marks.Remove(kind);
    }

    private static RunningMark Mark(string exe = Exe, int pid = 4242) => new(pid, 638000000000000000, exe);

    private RelaunchKind Schedule(bool stillRunning = false, bool serviceInstalled = false, int? setup = SetupId, Func<TimeSpan?>? heartbeat = null)
        => RelaunchAfterSetup.Schedule(new RunningMarks(_store), Exe, _ => stillRunning, () => serviceInstalled, () => setup, heartbeat ?? (() => null), @"C:\logs\install-hook.log", _started.Add, _log.Add);

    [Fact]
    public void A_tray_that_was_running_is_started_again_once_Setup_has_exited()
    {
        new RunningMarks(_store).Set(RunningKind.Tray, Mark());

        var kind = Schedule();

        Assert.Equal(RelaunchKind.Tray, kind);
        var helper = Assert.Single(_started);
        var script = string.Join(' ', helper.ArgumentList);
        Assert.Contains($"-Id {SetupId}", script);
        Assert.Contains("WaitForExit", script);
        Assert.Contains(RelaunchAfterSetup.Argument, script);
        Assert.Contains(Exe, script);
        Assert.Contains(_log, l => l.Contains("was running before this install") && l.Contains($"pid {SetupId}") && l.Contains("unless Setup starts it"));
    }

    [Fact]
    public void A_service_that_was_running_is_restarted_as_a_service_not_as_a_tray()
    {
        new RunningMarks(_store).Set(RunningKind.Service, Mark());

        var kind = Schedule(serviceInstalled: true);

        Assert.Equal(RelaunchKind.Service, kind);
        var script = string.Join(' ', Assert.Single(_started).ArgumentList);
        Assert.Contains("Start-Service", script);
        Assert.DoesNotContain(RelaunchAfterSetup.Argument, script);
        Assert.Contains(_log, l => l.Contains("service") && l.Contains("was running before this install"));
    }

    [Fact]
    public void A_service_mark_with_no_service_installed_starts_nothing()
    {
        new RunningMarks(_store).Set(RunningKind.Service, Mark());

        Assert.Equal(RelaunchKind.None, Schedule(serviceInstalled: false));
        Assert.Empty(_started);
    }

    [Fact]
    public void Nothing_running_before_means_nothing_is_started()
    {
        Assert.Equal(RelaunchKind.None, Schedule());

        Assert.Empty(_started);
        Assert.Contains(_log, l => l.Contains("was not running") && l.Contains("left stopped"));
    }

    [Fact]
    public void A_Deluno_run_from_somewhere_else_is_not_this_installs_and_is_left_alone()
    {
        new RunningMarks(_store).Set(RunningKind.Tray, Mark(@"D:\portable\Deluno.exe"));

        Assert.Equal(RelaunchKind.None, Schedule());
        Assert.Empty(_started);
        Assert.NotNull(_store.Read(RunningKind.Tray));
    }

    [Fact]
    public void A_hook_run_from_the_versions_own_folder_still_finds_the_mark_of_the_current_folder()
    {
        new RunningMarks(_store).Set(RunningKind.Tray, Mark(@"C:\Users\Example\AppData\Local\Deluno\current\Deluno.exe"));

        var kind = RelaunchAfterSetup.Schedule(new RunningMarks(_store), @"C:\Users\Example\AppData\Local\Deluno\app-1.0.0-rc.30.27\Deluno.exe", _ => false, () => false, () => SetupId, () => null, "x.log", _started.Add, _log.Add);

        Assert.Equal(RelaunchKind.Tray, kind);
        Assert.Contains(@"current\Deluno.exe", string.Join(' ', Assert.Single(_started).ArgumentList));
    }

    [Fact]
    public void A_Deluno_that_Setup_did_not_stop_is_still_running_and_needs_no_start()
    {
        new RunningMarks(_store).Set(RunningKind.Tray, Mark());

        Assert.Equal(RelaunchKind.None, Schedule(stillRunning: true));
        Assert.Empty(_started);
    }

    [Fact]
    public void When_both_marks_exist_only_the_service_is_started()
    {
        var marks = new RunningMarks(_store);
        marks.Set(RunningKind.Tray, Mark());
        marks.Set(RunningKind.Service, Mark(pid: 99));

        Assert.Equal(RelaunchKind.Service, Schedule(serviceInstalled: true));
        Assert.Single(_started);
    }

    [Fact]
    public void The_install_hook_and_the_update_hook_of_one_Setup_start_Deluno_once()
    {
        new RunningMarks(_store).Set(RunningKind.Tray, Mark());

        Schedule();
        Schedule();

        Assert.Single(_started);
        Assert.Null(_store.Read(RunningKind.Tray));
    }

    [Fact]
    public void An_unidentified_Setup_is_given_a_fixed_wait_instead_of_leaving_Deluno_stopped()
    {
        new RunningMarks(_store).Set(RunningKind.Tray, Mark());

        Assert.Equal(RelaunchKind.Tray, Schedule(setup: null));

        var script = string.Join(' ', Assert.Single(_started).ArgumentList);
        Assert.DoesNotContain("-Id ", script);
        Assert.Contains("Start-Sleep", script);
        Assert.Contains(_log, l => l.Contains("could not be identified"));
    }

    [Fact]
    public void A_helper_that_cannot_start_is_logged_and_the_install_goes_on()
    {
        new RunningMarks(_store).Set(RunningKind.Tray, Mark());

        var kind = RelaunchAfterSetup.Schedule(new RunningMarks(_store), Exe, _ => false, () => false, () => SetupId, () => null, "x.log",
            _ => throw new System.ComponentModel.Win32Exception("no PowerShell"), _log.Add);

        Assert.Equal(RelaunchKind.None, kind);
        Assert.Contains(_log, l => l.Contains("could not be started again") && l.Contains("no PowerShell"));
    }

    [Fact]
    public async Task The_hook_never_takes_longer_than_its_budget_to_give_up()
    {
        var time = new FakeTimeProvider();
        using var gate = new ManualResetEventSlim(false);
        var result = Task.Run(() => RelaunchAfterSetup.RunWithinBudget(() => gate.Wait(), RelaunchAfterSetup.HookBudget, time, _log.Add));

        Assert.True(RelaunchAfterSetup.HookBudget < TimeSpan.FromSeconds(30), "Velopack kills a hook after 30 seconds");
        await Task.Delay(50);
        Assert.False(result.IsCompleted, "it gave up before its budget was spent");
        // Fake time, a second at a time, until the budget is spent (the timer is registered on the work's own thread).
        for (var spent = 0; spent < 40 && !result.IsCompleted; spent++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        Assert.True(result.IsCompleted, "it was still waiting after its budget was spent");
        Assert.False(await result);
        Assert.Contains(_log, l => l.Contains("budget"));
        gate.Set();
    }

    [Fact]
    public void Work_inside_the_budget_completes_normally_and_a_failure_is_logged_not_thrown()
    {
        var time = new FakeTimeProvider();
        Assert.True(RelaunchAfterSetup.RunWithinBudget(() => { }, RelaunchAfterSetup.HookBudget, time, _log.Add));
        Assert.False(RelaunchAfterSetup.RunWithinBudget(() => throw new IOException("busy"), RelaunchAfterSetup.HookBudget, time, _log.Add));
        Assert.Contains(_log, l => l.Contains("busy"));
    }

    [Fact]
    public void A_start_that_follows_Setup_steps_aside_only_when_Deluno_is_already_running()
    {
        Assert.True(RelaunchAfterSetup.ShouldStepAside(["--after-setup"], () => true));
        Assert.False(RelaunchAfterSetup.ShouldStepAside(["--after-setup"], () => false));
        var asked = false;
        Assert.False(RelaunchAfterSetup.ShouldStepAside([], () => asked = true));
        Assert.False(asked);
    }

    [Fact]
    public void The_running_mark_is_cleared_only_by_its_own_process()
    {
        var marks = new RunningMarks(_store);
        marks.Set(RunningKind.Tray, Mark(pid: 5));

        marks.Clear(RunningKind.Tray, 6);
        Assert.NotNull(_store.Read(RunningKind.Tray));
        marks.Clear(RunningKind.Tray, 5);
        Assert.Null(_store.Read(RunningKind.Tray));
    }

    // The helper really runs: these start the real PowerShell on Windows, with a stand-in for Deluno.exe.

    private string FakeDeluno(out string markerFile)
    {
        markerFile = Path.Combine(_folder, "started.txt");
        var cmd = Path.Combine(_folder, "Deluno.cmd");
        File.WriteAllText(cmd, $"@echo %* > \"{markerFile}\"\r\n");
        return cmd;
    }

    private static void RunHelper(RelaunchKind kind, int? setup, string exe, string logPath)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", RelaunchAfterSetup.HelperScript(kind, setup, exe, logPath) })
        {
            info.ArgumentList.Add(a);
        }
        using var p = Process.Start(info)!;
        Assert.True(p.WaitForExit(60000), "the helper did not finish");
    }

    [Fact]
    public void The_helper_waits_for_Setup_and_then_starts_Deluno_with_the_after_setup_argument()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var exe = FakeDeluno(out var marker);
        var logPath = Path.Combine(_folder, "install-hook.log");
        using var setup = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;

        RunHelper(RelaunchKind.Tray, setup.Id, exe, logPath);

        Assert.True(setup.HasExited, "the helper must not start Deluno before Setup has exited");
        Assert.True(SpinWait.SpinUntil(() => File.Exists(marker), 20000), "Deluno was not started");
        Assert.Contains("--after-setup", File.ReadAllText(marker));
        Assert.Contains("started Deluno", File.ReadAllText(logPath));
    }

    [Fact]
    public void The_helper_starts_nothing_when_Setup_has_started_Deluno_itself()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var running = Path.Combine(_folder, "Deluno.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), running);
        using var already = Process.Start(new ProcessStartInfo(running, "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        var logPath = Path.Combine(_folder, "install-hook.log");
        try
        {
            using var setup = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
            setup.WaitForExit();
            RunHelper(RelaunchKind.Tray, setup.Id, running, logPath);

            Assert.Contains("already running", File.ReadAllText(logPath));
            Assert.DoesNotContain("started Deluno", File.ReadAllText(logPath));
        }
        finally
        {
            already.Kill();
        }
    }

    [Fact]
    public void The_registry_store_round_trips_a_mark()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var store = new RegistryRunningMarkStore(@"SOFTWARE\DelunoTest_" + Guid.NewGuid().ToString("N"), @"SOFTWARE\DelunoTestSvc_" + Guid.NewGuid().ToString("N"), machineForService: false);
        try
        {
            Assert.Null(store.Read(RunningKind.Tray));
            store.Write(RunningKind.Tray, Mark());
            Assert.Equal(Mark(), store.Read(RunningKind.Tray));
            store.Remove(RunningKind.Tray);
            Assert.Null(store.Read(RunningKind.Tray));
        }
        finally
        {
            store.Remove(RunningKind.Tray);
        }
    }
}
