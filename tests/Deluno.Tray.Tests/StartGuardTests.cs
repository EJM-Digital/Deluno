using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Tray;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1256 around the supervisor: the watch that spots a hung start, the process restart and its loop guard, the Windows
/// service's non-zero exit, its recovery settings before the first start, and the tray's words while starting.
/// </summary>
public sealed class StartGuardTests : IDisposable
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-guard-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(Epoch);

    public StartGuardTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* temp */ }
    }

    private sealed class Log : ISupervisorLog
    {
        public List<string> Lines { get; } = [];
        public void Info(string message) => Lines.Add("INFO " + message);
        public void Warning(string message) => Lines.Add("WARN " + message);
        public void Error(string message) => Lines.Add("ERROR " + message);
        public override string ToString() => string.Join('\n', Lines);
    }

    private async Task PassAsync(TimeSpan span, TimeSpan step)
    {
        for (var spent = TimeSpan.Zero; spent < span; spent += step)
        {
            _time.Advance(step);
            await Task.Yield();
            Thread.Yield();
        }
    }

    // ------------------------------------------------------------------ the hang watch

    [Fact]
    public async Task The_watch_reports_a_start_that_made_no_progress_for_its_window_and_the_snapshot_says_where()
    {
        var progress = new StartProgress(_time, null, pid: 7);
        progress.Enter(StartupPhase.OpeningDatabases, "series");
        var windows = new StartWindows();

        var watching = StartHangWatch.WaitForHangAsync(progress, windows, _time, CancellationToken.None);
        await PassAsync(windows.OpeningDatabases - TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        Assert.False(watching.IsCompleted);
        await PassAsync(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5));
        var snapshot = await watching.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(snapshot);
        Assert.Equal(StartupPhase.OpeningDatabases, snapshot!.Phase);
        Assert.Equal("series", snapshot.Detail);
    }

    [Fact]
    public async Task The_watch_restarts_its_window_whenever_progress_moves()
    {
        var progress = new StartProgress(_time, null, pid: 7);
        var windows = new StartWindows();
        progress.Enter(StartupPhase.Migrations, "platform: V1");
        var watching = StartHangWatch.WaitForHangAsync(progress, windows, _time, CancellationToken.None);

        for (var i = 2; i <= 12; i++)
        {
            await PassAsync(windows.Migrations - TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(20));
            progress.Enter(StartupPhase.Migrations, "platform: V" + i);
        }

        Assert.False(watching.IsCompleted);
        progress.Enter(StartupPhase.Ready);
        await PassAsync(TimeSpan.FromHours(1), TimeSpan.FromMinutes(1));
        Assert.Null(await watching.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task The_watch_ends_quietly_when_it_is_cancelled()
    {
        var progress = new StartProgress(_time, null, pid: 7);
        using var stop = new CancellationTokenSource();
        var watching = StartHangWatch.WaitForHangAsync(progress, new StartWindows(), _time, stop.Token);

        stop.Cancel();

        Assert.Null(await watching.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // ------------------------------------------------------------------ process restart and its guard

    private sealed class Launcher(bool fail = false) : IProcessLauncher
    {
        public List<(string Exe, string Arguments)> Launched { get; } = [];

        public void Launch(string exePath, string arguments)
        {
            if (fail)
            {
                throw new InvalidOperationException("cannot launch");
            }

            Launched.Add((exePath, arguments));
        }
    }

    private ProcessRelaunch Relaunch(Launcher launcher, Log? log = null)
        => new(_folder, _time, launcher, pid: 321, exePath: @"C:\Apps\Deluno\Deluno.exe", log ?? new Log());

    [Fact]
    public void A_relaunch_starts_Deluno_again_telling_it_which_process_to_wait_for()
    {
        var launcher = new Launcher();

        var ok = Relaunch(launcher).TryRestart("it stopped while updating the databases");

        Assert.True(ok);
        var (exe, arguments) = Assert.Single(launcher.Launched);
        Assert.Equal(@"C:\Apps\Deluno\Deluno.exe", exe);
        Assert.Contains(ProcessRelaunch.AfterRestartArgument + " 321", arguments);
    }

    [Fact]
    public void The_relaunch_guard_allows_two_in_half_an_hour_and_refuses_a_third_so_it_never_loops()
    {
        var launcher = new Launcher();
        var log = new Log();
        var relaunch = Relaunch(launcher, log);

        Assert.True(relaunch.TryRestart("hung"));
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(relaunch.TryRestart("hung again"));
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(relaunch.TryRestart("hung a third time"));

        Assert.Equal(2, launcher.Launched.Count);
        Assert.Contains("ERROR", log.ToString());
        Assert.Contains("not restart", log.ToString());
    }

    [Fact]
    public void The_relaunch_guard_forgets_old_relaunches_after_its_window()
    {
        var launcher = new Launcher();
        var relaunch = Relaunch(launcher);
        Assert.True(relaunch.TryRestart("a"));
        Assert.True(relaunch.TryRestart("b"));
        Assert.False(relaunch.TryRestart("c"));

        _time.Advance(ProcessRelaunch.GuardWindow + TimeSpan.FromSeconds(1));

        Assert.True(relaunch.TryRestart("d"));
    }

    [Fact]
    public void The_relaunch_guard_survives_the_process_because_it_is_a_file()
    {
        var launcher = new Launcher();
        Assert.True(Relaunch(launcher).TryRestart("a"));
        Assert.True(Relaunch(launcher).TryRestart("b"));

        Assert.False(Relaunch(launcher).TryRestart("c")); // a new instance, as after the relaunch
    }

    [Fact]
    public void A_launch_that_fails_is_not_counted_and_is_reported_as_not_restarted()
    {
        var launcher = new Launcher(fail: true);
        var log = new Log();

        Assert.False(Relaunch(launcher, log).TryRestart("hung"));
        // The failed launch used none of the allowance.
        var working = new Launcher();
        Assert.True(Relaunch(working).TryRestart("a"));
        Assert.True(Relaunch(working).TryRestart("b"));
        Assert.Contains("cannot launch", log.ToString());
    }

    [Fact]
    public void A_damaged_guard_file_does_not_block_a_restart()
    {
        File.WriteAllText(Path.Combine(_folder, ProcessRelaunch.GuardFileName), "{\"launche");

        Assert.True(Relaunch(new Launcher()).TryRestart("hung"));
    }

    [Theory]
    [InlineData("--after-restart", "4242", 4242)]
    [InlineData("--port", "9000", null)]
    public void A_relaunched_Deluno_finds_out_which_process_it_replaces(string flag, string value, int? expected)
    {
        Assert.Equal(expected, ProcessRelaunch.ParentToWaitFor([flag, value]));
    }

    // ------------------------------------------------------------------ service mode

    [Fact]
    public async Task A_hung_start_in_service_mode_ends_in_a_non_zero_exit_after_logging_where_it_was_stuck()
    {
        var progress = new StartProgress(_time, null, pid: 7);
        progress.Enter(StartupPhase.Migrations, "movies: V0042");
        var log = new Log();
        var exits = new List<int>();
        var windows = new StartWindows();

        var guard = ServiceStartGuard.WatchAsync(progress, windows, _time, log, () => "threads: 12", exits.Add, CancellationToken.None);
        await PassAsync(windows.Migrations + TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        await guard.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([ServiceStartGuard.HungExitCode], exits);
        Assert.NotEqual(0, ServiceStartGuard.HungExitCode);
        Assert.Contains("updating the databases", log.ToString());
        Assert.Contains("movies: V0042", log.ToString());
        Assert.Contains("threads: 12", log.ToString());
        Assert.Contains("ERROR", log.ToString());
    }

    [Fact]
    public async Task A_service_start_that_gets_ready_never_exits()
    {
        var progress = new StartProgress(_time, null, pid: 7);
        var exits = new List<int>();
        var guard = ServiceStartGuard.WatchAsync(progress, new StartWindows(), _time, new Log(), () => "", exits.Add, CancellationToken.None);

        progress.Enter(StartupPhase.Ready);
        await PassAsync(TimeSpan.FromHours(2), TimeSpan.FromMinutes(1));
        await guard.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(exits);
    }

    [Fact]
    public void A_service_start_that_fails_logs_the_reason_and_exits_non_zero_so_the_service_manager_recovers()
    {
        var log = new Log();
        var exits = new List<int>();

        ServiceStartGuard.OnStartFailed(new InvalidOperationException("address already in use"), log, exits.Add);

        Assert.Equal([ServiceStartGuard.FailedExitCode], exits);
        Assert.NotEqual(0, ServiceStartGuard.FailedExitCode);
        Assert.Contains("address already in use", log.ToString());
    }

    private sealed class FakeSc(params int?[] exits) : ServiceManager.IScRunner
    {
        public List<string> Ran { get; } = [];

        public int? Run(string arguments, TimeSpan timeout)
        {
            Ran.Add(arguments);
            return exits[Math.Min(Ran.Count - 1, exits.Length - 1)];
        }
    }

    [Fact]
    public async Task Recovery_settings_are_set_early_and_the_answer_is_kept_for_Health()
    {
        var sc = new FakeSc(0);
        var warnings = new List<string>();

        var problem = await ServiceManager.EnsureFailureActionsEarlyAsync(warnings.Add, sc, TimeSpan.FromSeconds(1));

        Assert.Null(problem);
        Assert.Equal(2, sc.Ran.Count);

        var refused = await ServiceManager.EnsureFailureActionsEarlyAsync(warnings.Add, new FakeSc(5), TimeSpan.FromSeconds(1));
        Assert.Contains("code 5", refused);
        Assert.Single(warnings);
    }

    [Fact]
    public void The_service_sets_its_recovery_settings_before_it_builds_and_starts_the_host()
    {
        var source = File.ReadAllText(RepoFile("apps/windows-tray/ServiceHost.cs"));

        var early = source.IndexOf("EnsureFailureActionsEarlyAsync", StringComparison.Ordinal);
        var build = source.IndexOf("builder.Build()", StringComparison.Ordinal);
        var run = source.IndexOf("app.RunAsync()", StringComparison.Ordinal);

        Assert.True(early > 0, "ServiceHost must set the recovery settings itself");
        Assert.True(early < build, "the recovery settings come before the host is built, so an older install has them even if the start hangs");
        Assert.True(build < run);
        Assert.Contains("ServiceStartGuard.WatchAsync", source);
        Assert.Contains("ServiceStartGuard.OnStartFailed", source);
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Deluno.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    // ------------------------------------------------------------------ the tray's words

    [Fact]
    public void While_starting_the_tray_says_what_it_is_doing_instead_of_a_blank_wait()
    {
        var status = new TrayStatus { State = TrayState.Starting, StartNote = StartupPhases.Words(StartupPhase.PreUpdateBackup) };

        Assert.Equal("Deluno - Starting - backing up your data before the update...", TrayHoverText.Build(status));
        Assert.Contains("backing up your data", TrayHoverText.StatusWords(status));
        Assert.True(TrayHoverText.Build(status).Length <= TrayHoverText.Limit);
    }

    [Fact]
    public void Without_a_note_the_wording_is_unchanged()
    {
        Assert.Equal("Deluno - Starting...", TrayHoverText.Build(new TrayStatus()));
    }

    [Fact]
    public void A_retry_note_still_wins_over_the_phase_note()
    {
        var status = new TrayStatus { State = TrayState.Starting, RestartNote = "starting again (try 2 of 5)", StartNote = "opening the databases" };

        Assert.Contains("try 2 of 5", TrayHoverText.Build(status));
    }
}
