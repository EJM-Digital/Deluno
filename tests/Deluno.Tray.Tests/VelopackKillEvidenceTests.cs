using System.Diagnostics;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1256: a Deluno from before the running marks (rc.30.25 in the golden-path update scenario) leaves no mark, so the first
/// update from it has only Velopack's own log to say that Deluno was running. The lines below are copied from real logs:
/// a Setup over a running Deluno (installer-rc23 run 11) and an Update.exe over a running Weir.
/// </summary>
public sealed class VelopackKillEvidenceTests
{
    private const string Exe = @"C:\Users\Administrator\AppData\Local\Deluno\current\Deluno.exe";

    private static readonly string[] RealLog =
    [
        @"[setup:7604] [22:33:29] [INFO] Starting process install hook",
        @"[setup:7604] [22:34:05] [INFO] Checking for running processes in: C:\Users\Administrator\AppData\Local\Deluno",
        @"[setup:7604] [22:34:05] [WARN] Failed to get long path for 'c:\windows\system32\securityhealthservice.exe': The system cannot find the file specified. (0x80070002)",
        @"[setup:7604] [22:34:05] [WARN] Killing process: C:\Users\Administrator\AppData\Local\Deluno\current\Deluno.exe (3036)",
        @"[setup:7604] [22:34:05] [WARN] Retrying operation in 1000ms... (error was: Some([5] Access is denied.))",
    ];

    [Fact]
    public void The_Deluno_that_this_Setup_ended_is_found_by_the_Setup_id_in_the_real_log()
    {
        var found = VelopackKillEvidence.Find(RealLog, 7604, Exe);

        Assert.Equal(3036, found?.ProcessId);
        Assert.Equal(Exe, found?.Executable);
    }

    [Fact]
    public void A_kill_by_some_other_Setup_is_not_this_Setups()
        => Assert.Null(VelopackKillEvidence.Find(RealLog, 7605, Exe));

    [Fact]
    public void Another_apps_process_and_a_skipped_self_are_not_Deluno()
    {
        string[] lines =
        [
            @"[update:2460] [08:20:24] [WARN] Killing process: C:\Users\Administrator\AppData\Local\Weir\current\Weir.exe (6064)",
            @"[update:2460] [08:20:24] [WARN] Killing process: C:\Users\Administrator\AppData\Local\Weir\current\server\WeirServer.exe (3456)",
            @"[update:2460] [08:20:24] [WARN] Skipping killing self: C:\Users\Administrator\AppData\Local\Deluno\Update.exe (2460)",
        ];

        Assert.Null(VelopackKillEvidence.Find(lines, 2460, Exe));
    }

    [Fact]
    public void An_Update_exe_kill_counts_the_same_way()
    {
        string[] lines = [@"[update:2460] [08:20:24] [WARN] Killing process: C:\Users\Administrator\AppData\Local\Deluno\current\Deluno.exe (6064)"];

        Assert.Equal(6064, VelopackKillEvidence.Find(lines, 2460, Exe)?.ProcessId);
    }

    [Fact]
    public void A_Deluno_run_from_another_folder_is_not_this_install()
    {
        string[] lines = [@"[setup:7604] [22:34:05] [WARN] Killing process: D:\portable\Deluno.exe (3036)"];

        Assert.Null(VelopackKillEvidence.Find(lines, 7604, Exe));
    }

    private readonly List<ProcessStartInfo> _started = [];
    private readonly List<string> _log = [];

    private RelaunchKind Schedule(bool serviceInstalled, Func<int, RunningMark?> killed, bool stillRunning = false)
        => RelaunchAfterSetup.Schedule(new RunningMarks(new Store()), Exe, _ => stillRunning, () => serviceInstalled, () => 7604, killed, "x.log", _started.Add, _log.Add);

    [Fact]
    public void A_Deluno_with_no_mark_that_Velopack_ended_is_started_again_as_a_tray()
    {
        var kind = Schedule(serviceInstalled: false, setup => VelopackKillEvidence.Find(RealLog, setup, Exe));

        Assert.Equal(RelaunchKind.Tray, kind);
        Assert.Single(_started);
        Assert.Contains(_log, l => l.Contains("Velopack's log says Setup (pid 7604) ended Deluno (pid 3036)"));
        Assert.Contains(_log, l => l.Contains("was running before this install") && l.Contains("unless Setup starts it"));
    }

    [Fact]
    public void A_service_install_with_no_mark_that_Velopack_ended_is_started_again_as_a_service()
        => Assert.Equal(RelaunchKind.Service, Schedule(serviceInstalled: true, setup => VelopackKillEvidence.Find(RealLog, setup, Exe)));

    [Fact]
    public void Without_a_mark_or_a_kill_in_the_log_nothing_is_started()
    {
        Assert.Equal(RelaunchKind.None, Schedule(serviceInstalled: false, _ => null));
        Assert.Empty(_started);
    }

    [Fact]
    public void A_Deluno_the_log_says_was_ended_but_that_is_running_again_is_not_started_a_second_time()
    {
        Assert.Equal(RelaunchKind.None, Schedule(serviceInstalled: false, setup => VelopackKillEvidence.Find(RealLog, setup, Exe), stillRunning: true));
        Assert.Empty(_started);
    }

    private sealed class Store : IRunningMarkStore
    {
        public RunningMark? Read(RunningKind kind) => null;
        public void Write(RunningKind kind, RunningMark mark) { }
        public void Remove(RunningKind kind) { }
    }
}
