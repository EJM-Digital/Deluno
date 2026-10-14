using System.Text.RegularExpressions;
using Deluno.Infrastructure.Storage;
using Deluno.Tray;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1256: every start of the web host (first start, Restart, Change port, a stop it did not choose) goes through one
/// supervisor. All of it runs on a fake clock: nothing here sleeps on the wall clock.
/// </summary>
public sealed class HostSupervisorTests
{
    // ------------------------------------------------------------------ harness

    internal sealed class HostCall(int attempt, CancellationToken token, StartProgress progress, FakeTimeProvider time)
    {
        public int Attempt { get; } = attempt;
        public CancellationToken Token { get; } = token;
        public StartProgress Progress { get; } = progress;
        public FakeTimeProvider Time { get; } = time;

        public void Enter(StartupPhase phase, string? detail = null) => Progress.Enter(phase, detail);

        /// <summary>Works for this long on the fake clock, as a real step does.</summary>
        public Task WorkAsync(TimeSpan span) => Task.Delay(span, Time, Token); // reason: already on the fake clock: a step that works for this long in fake time

        /// <summary>Never returns on its own; ends only when the supervisor cancels it.</summary>
        public Task HangAsync()
        {
            var tcs = new TaskCompletionSource();
            Token.Register(() => tcs.TrySetCanceled(Token));
            return tcs.Task;
        }
    }

    internal sealed class ScriptedHost : IStartableHost
    {
        public Func<HostCall, Task> Script { get; set; } = _ => Task.CompletedTask;
        public Func<CancellationToken, Task> OnFree { get; set; } = _ => Task.CompletedTask;
        public int Starts;
        public int Stops;
        public int Frees;
        public List<DateTimeOffset> StartTimes { get; } = [];
        public List<DateTimeOffset> CancelTimes { get; } = [];
        public int StartThreadId { get; private set; }
        public bool StartedOnPoolThread { get; private set; }
        public StartProgress Progress { get; set; } = null!;
        public FakeTimeProvider Time { get; set; } = null!;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref Starts);
            StartThreadId = Environment.CurrentManagedThreadId;
            StartedOnPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            lock (StartTimes)
            {
                StartTimes.Add(Time.GetUtcNow());
            }

            cancellationToken.Register(() =>
            {
                lock (CancelTimes)
                {
                    CancelTimes.Add(Time.GetUtcNow());
                }
            });
            return Script(new HostCall(attempt, cancellationToken, Progress, Time));
        }

        public Task StopAsync()
        {
            Interlocked.Increment(ref Stops);
            return Task.CompletedTask;
        }

        public Task FreeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Frees);
            return OnFree(cancellationToken);
        }
    }

    internal sealed class Restarter(bool allow) : IProcessRestarter
    {
        public List<string> Reasons { get; } = [];

        public bool TryRestart(string reason)
        {
            Reasons.Add(reason);
            return allow;
        }
    }

    internal sealed class Harness : IDisposable
    {
        public static readonly DateTimeOffset Epoch = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);

        public FakeTimeProvider Time { get; }
        public StartProgress Progress { get; }
        public ScriptedHost Host { get; } = new();
        public List<string> Lines { get; } = [];
        public List<string> Events { get; } = [];
        public List<StartAttemptInfo> Attempts { get; } = [];
        public HostSupervisor Supervisor { get; }
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "deluno-supervisor-" + Guid.NewGuid().ToString("N"));
        public string MarkerPath => Path.Combine(Folder, StartMarkerFile.FileName);

        public Harness(HostSupervisorSettings? settings = null, IProcessRestarter? restarter = null, double random = 0.5, int pid = 4242, bool offThread = false, FakeTimeProvider? time = null)
        {
            Time = time ?? new FakeTimeProvider(Epoch);
            Directory.CreateDirectory(Folder);
            Progress = new StartProgress(Time, MarkerPath, pid, line => { lock (Lines) { Lines.Add("progress: " + line); } });
            Host.Progress = Progress;
            Host.Time = Time;
            var log = new RecordingLog(Lines, Time);
            Supervisor = new HostSupervisor(
                Host,
                Progress,
                new HostRestartReporter
                {
                    Attempting = info => { lock (Events) { Events.Add($"attempting {info.Attempt}/{info.Limit} {info.Reason} delay={info.Delay.TotalSeconds:0}"); Attempts.Add(info); } },
                    AttemptFailed = (info, why) => { lock (Events) { Events.Add($"failed {info.Attempt}"); } },
                    Recovered = (reason, attempts) => { lock (Events) { Events.Add($"recovered {attempts}"); } },
                    GaveUp = (failures, window, why) => { lock (Events) { Events.Add($"gave up {failures}"); } },
                    Stable = () => { lock (Events) { Events.Add("stable"); } },
                    ProcessRestarting = why => { lock (Events) { Events.Add("process restart"); } }
                },
                log,
                Time,
                settings,
                restarter,
                diagnostics: () => "threads: 31 (diagnostics for the test)",
                random: () => random,
                runOffThread: offThread ? null : InlineRun);
        }

        /// <summary>
        /// Runs a try on the caller's thread so the fake clock and the script cannot race. The tests that are about the
        /// thread pool use <c>offThread: true</c> and never move the clock while the script runs.
        /// </summary>
        public static Task InlineRun(Func<Task> work)
        {
            // No synchronization context, so the script's own awaits resume on whichever thread completes them (as
            // they do on the thread pool) and never wait for the test's thread to come round.
            var prior = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                return work();
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prior);
            }
        }

        /// <summary>
        /// Moves the fake clock with no synchronization context on this thread. The timers fire here, and a continuation
        /// is only run in place (rather than queued to the thread pool, where it can lose a race with this loop) when the
        /// thread completing the task has none.
        /// </summary>
        public void AdvanceInline(TimeSpan span)
        {
            var prior = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                Time.Advance(span);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(prior);
            }
        }

        public string Log => string.Join('\n', Lines.ToArray());

        public void Dispose()
        {
            try { Directory.Delete(Folder, recursive: true); } catch { /* temp */ }
        }

        /// <summary>
        /// Moves the fake clock forward a little at a time until the task is done. Nothing sleeps; the loop gives the
        /// supervisor's thread-pool continuations a turn between steps. The limit stops a hung test.
        /// </summary>
        public async Task<T> DriveAsync<T>(Task<T> task, TimeSpan? step = null, TimeSpan? limit = null)
        {
            var each = step ?? TimeSpan.FromSeconds(1);
            var spent = TimeSpan.Zero;
            var cap = limit ?? TimeSpan.FromHours(12);
            while (!task.IsCompleted)
            {
                Assert.True(spent <= cap, "the supervisor did not finish within " + cap + " of fake time.\n" + Log);
                AdvanceInline(each);
                spent += each;
                await Task.Yield();
                Thread.Yield();
            }

            return await task;
        }

        /// <summary>Advances the fake clock by <paramref name="span"/> in small steps, giving continuations a turn.</summary>
        public async Task PassAsync(TimeSpan span, TimeSpan? step = null)
        {
            var each = step ?? TimeSpan.FromSeconds(1);
            for (var spent = TimeSpan.Zero; spent < span; spent += each)
            {
                AdvanceInline(each);
                await Task.Yield();
                Thread.Yield();
            }
        }

        /// <summary>Lets already-ready continuations run without moving the clock.</summary>
        public async Task SettleAsync()
        {
            for (var i = 0; i < 50; i++)
            {
                await Task.Yield();
                Thread.Yield();
            }
        }
    }

    private sealed class RecordingLog(List<string> lines, FakeTimeProvider? time = null) : ISupervisorLog
    {
        private string Stamp => time is null ? string.Empty : $"[+{(time.GetUtcNow() - Harness.Epoch).TotalSeconds:0}s] ";
        public void Info(string message) { lock (lines) { lines.Add(Stamp + "INFO " + message); } }
        public void Warning(string message) { lock (lines) { lines.Add(Stamp + "WARN " + message); } }
        public void Error(string message) { lock (lines) { lines.Add(Stamp + "ERROR " + message); } }
    }

    private static readonly StartupPhase[] AllPhases =
    [
        StartupPhase.Starting,
        StartupPhase.PreUpdateBackup,
        StartupPhase.OpeningDatabases,
        StartupPhase.Migrations,
        StartupPhase.WebHost
    ];

    public static TheoryData<StartupPhase> Phases()
    {
        var data = new TheoryData<StartupPhase>();
        foreach (var phase in AllPhases)
        {
            data.Add(phase);
        }

        return data;
    }

    // ------------------------------------------------------------------ the happy start

    [Fact]
    public async Task A_start_that_works_is_one_try_with_no_wait_and_no_warning()
    {
        using var h = new Harness();
        h.Host.Script = call => { call.Enter(StartupPhase.Ready); return Task.CompletedTask; };

        var result = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.FirstStart));

        Assert.Equal(StartOutcome.Started, result.Outcome);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, h.Host.Starts);
        Assert.InRange(h.Host.StartTimes[0], Harness.Epoch, Harness.Epoch + TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.Zero, h.Attempts[0].Delay);
        Assert.DoesNotContain("WARN", h.Log);
        Assert.Matches(new Regex(@"INFO .*try 1 of 5"), h.Log);
        Assert.Contains("INFO Deluno started", h.Log);
        Assert.Equal(["attempting 1/5 FirstStart delay=0", "recovered 1"], h.Events);
    }

    [Fact]
    public async Task The_host_is_started_off_the_calling_thread()
    {
        using var h = new Harness(offThread: true);
        h.Host.Script = call => { call.Enter(StartupPhase.Ready); return Task.CompletedTask; };
        var caller = Environment.CurrentManagedThreadId;

        // The caller (the tray's UI thread) must not be the thread that runs the host's start. The clock is not moved.
        var result = await h.Supervisor.StartAsync(StartReason.FirstStart).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(StartOutcome.Started, result.Outcome);
        Assert.True(h.Host.StartedOnPoolThread, "the start must run on the thread pool, never on the caller's thread");
        Assert.NotEqual(caller, h.Host.StartThreadId);
    }

    [Fact]
    public async Task A_start_that_blocks_its_thread_does_not_block_the_supervisor()
    {
        using var h = new Harness(offThread: true);
        using var release = new ManualResetEventSlim();
        h.Host.Script = call =>
        {
            call.Enter(StartupPhase.Migrations, "platform: V0001");
            release.Wait(TimeSpan.FromSeconds(5)); // a synchronous, blocking step, as a slow migration is (bounded so a broken supervisor fails the test instead of hanging it)
            call.Enter(StartupPhase.Ready);
            return Task.CompletedTask;
        };

        var running = h.Supervisor.StartAsync(StartReason.FirstStart);
        await h.SettleAsync();
        Assert.False(running.IsCompleted);
        release.Set();

        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(StartOutcome.Started, result.Outcome);
    }

    // ------------------------------------------------------------------ failures

    [Fact]
    public async Task An_instant_failure_is_tried_again_after_a_pause_and_the_retry_is_a_warning()
    {
        using var h = new Harness();
        h.Host.Script = call =>
        {
            if (call.Attempt == 1)
            {
                throw new InvalidOperationException("exited at once");
            }

            call.Enter(StartupPhase.Ready);
            return Task.CompletedTask;
        };

        var result = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.FirstStart));

        Assert.Equal(StartOutcome.Started, result.Outcome);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, h.Host.Starts);
        var waited = h.Host.StartTimes[1] - h.Host.StartTimes[0];
        // 15 s base, half jitter: between 7.5 and 15 seconds.
        Assert.InRange(waited, TimeSpan.FromSeconds(7.5), TimeSpan.FromSeconds(17));
        Assert.Matches(new Regex(@"WARN .*try 1 of 5 failed.*exited at once.*again in \d+ seconds"), h.Log);
        Assert.Matches(new Regex(@"WARN .*started on try 2 of 5"), h.Log);
        Assert.Equal("attempting 1/5 FirstStart delay=0", h.Events[0]);
        Assert.Equal("failed 1", h.Events[1]);
        Assert.StartsWith("attempting 2/5 FirstStart delay=", h.Events[2]);
        Assert.Equal("recovered 2", h.Events[3]);
    }

    [Theory]
    [MemberData(nameof(Phases))]
    public async Task A_failure_in_any_phase_names_the_phase_and_is_tried_again(StartupPhase phase)
    {
        using var h = new Harness();
        h.Host.Script = call =>
        {
            if (call.Attempt == 1)
            {
                call.Enter(phase, "the step");
                throw new InvalidOperationException("the step failed");
            }

            call.Enter(StartupPhase.Ready);
            return Task.CompletedTask;
        };

        var result = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.FirstStart));

        Assert.Equal(StartOutcome.Started, result.Outcome);
        Assert.Equal(2, h.Host.Starts);
        Assert.Contains("while " + StartupPhases.Words(phase), h.Log);
        Assert.Contains("the step failed", h.Log);
        Assert.Equal(1, h.Host.Frees);
    }

    [Fact]
    public async Task A_failed_start_releases_the_host_before_waiting_so_the_databases_and_port_are_free()
    {
        using var h = new Harness();
        var freedBeforeSecondStart = false;
        h.Host.Script = call =>
        {
            if (call.Attempt == 2)
            {
                freedBeforeSecondStart = h.Host.Frees >= 1;
                call.Enter(StartupPhase.Ready);
                return Task.CompletedTask;
            }

            throw new InvalidOperationException("port in use");
        };

        await h.DriveAsync(h.Supervisor.StartAsync(StartReason.FirstStart));

        Assert.True(freedBeforeSecondStart);
    }

    // ------------------------------------------------------------------ hangs

    [Theory]
    [MemberData(nameof(Phases))]
    public async Task A_hang_in_any_phase_is_cut_off_after_its_window_then_the_host_is_freed_and_tried_again(StartupPhase phase)
    {
        using var h = new Harness();
        var window = h.Supervisor.Settings.Windows.For(phase);
        DateTimeOffset entered = default;
        h.Host.Script = async call =>
        {
            if (call.Attempt == 1)
            {
                call.Enter(phase, "stuck here");
                entered = call.Time.GetUtcNow();
                await call.HangAsync();
                return;
            }

            call.Enter(StartupPhase.Ready);
        };

        var result = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.FirstStart));

        Assert.True(result.Outcome == StartOutcome.Started, result.Outcome + " " + h.Log);
        Assert.Equal(2, h.Host.Starts);
        var cancelledAfter = h.Host.CancelTimes[0] - entered;
        Assert.True(cancelledAfter >= window, $"cancelled after {cancelledAfter}, before the {window} window ended");
        Assert.True(cancelledAfter <= window + TimeSpan.FromSeconds(3), $"cancelled after {cancelledAfter}, long after the {window} window {h.Log}");
        Assert.Equal(1, h.Host.Frees);
        // The snapshot says where it was stuck.
        Assert.Contains("no progress", h.Log);
        Assert.Contains(StartupPhases.Words(phase), h.Log);
        Assert.Contains("stuck here", h.Log);
        Assert.Contains("threads: 31", h.Log);
        Assert.Matches(new Regex(@"WARN .*started on try 2"), h.Log);
    }

    [Fact]
    public async Task A_slow_migration_that_keeps_reporting_progress_is_never_cut_off()
    {
        using var h = new Harness();
        // 30 migrations at 5 minutes each: 150 minutes, ten times the migration window, every step reported.
        h.Host.Script = async call =>
        {
            for (var i = 1; i <= 30; i++)
            {
                call.Enter(StartupPhase.Migrations, $"platform: V{i:0000}");
                await call.WorkAsync(TimeSpan.FromMinutes(5));
            }

            call.Enter(StartupPhase.Ready);
        };

        var result = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.FirstStart), step: TimeSpan.FromSeconds(10));

        Assert.Equal(StartOutcome.Started, result.Outcome);
        Assert.Equal(1, h.Host.Starts);
        Assert.Empty(h.Host.CancelTimes);
        Assert.Equal(0, h.Host.Frees);
    }

    [Fact]
    public async Task One_silent_step_just_under_its_window_is_not_a_hang()
    {
        using var h = new Harness();
        var window = h.Supervisor.Settings.Windows.Migrations;
        h.Host.Script = async call =>
        {
            call.Enter(StartupPhase.Migrations, "games: V0200 (a very large table)");
            await call.WorkAsync(window - TimeSpan.FromSeconds(30));
            call.Enter(StartupPhase.Ready);
        };

        var result = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.FirstStart));

        Assert.Equal(StartOutcome.Started, result.Outcome);
        Assert.Equal(1, h.Host.Starts);
        Assert.Empty(h.Host.CancelTimes);
    }

    [Fact]
    public async Task A_hung_start_that_ignores_cancel_is_freed_by_stopping_and_disposing_the_host()
    {
        using var h = new Harness();
        var stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Host.Script = call =>
        {
            if (call.Attempt == 1)
            {
                call.Enter(StartupPhase.OpeningDatabases, "games");
                return stuck.Task; // does not look at the token
            }

            call.Enter(StartupPhase.Ready);
            return Task.CompletedTask;
        };
        // Disposing the host is what makes the stuck start give up its databases.
        h.Host.OnFree = _ => { stuck.TrySetException(new ObjectDisposedException("host")); return Task.CompletedTask; };

        var result = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.FirstStart));

        Assert.Equal(StartOutcome.Started, result.Outcome);
        Assert.Equal(1, h.Host.Frees);
    }

    [Fact]
    public async Task A_hung_host_that_cannot_be_freed_restarts_the_process()
    {
        var restarter = new Restarter(allow: true);
        using var h = new Harness(restarter: restarter);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Host.Script = call =>
        {
            call.Enter(StartupPhase.Migrations, "movies: V0042");
            return never.Task; // ignores cancel
        };
        h.Host.OnFree = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct); // the host will not let go // reason: the host never lets go; only the supervisor's cancellation ends it

        var result = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.FirstStart));

        Assert.Equal(StartOutcome.ProcessRestarting, result.Outcome);
        var reason = Assert.Single(restarter.Reasons);
        Assert.Contains("updating the databases", reason);
        Assert.Contains("process restart", h.Events);
        Assert.Matches(new Regex(@"ERROR .*could not be freed.*restart"), h.Log);
        Assert.Equal(1, h.Host.Starts);
    }

    [Fact]
    public async Task When_the_process_restart_is_refused_by_its_loop_guard_the_supervisor_gives_up_in_words()
    {
        var restarter = new Restarter(allow: false);
        using var h = new Harness(restarter: restarter);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Host.Script = call => { call.Enter(StartupPhase.WebHost); return never.Task; };
        h.Host.OnFree = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct); // reason: the host never lets go; only the supervisor's cancellation ends it

        var result = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.FirstStart));

        Assert.Equal(StartOutcome.GaveUp, result.Outcome);
        Assert.Single(restarter.Reasons);
        Assert.StartsWith("gave up", h.Events[^1]);
    }

    [Fact]
    public async Task A_hang_when_no_process_restarter_is_given_gives_up_rather_than_leaving_a_half_started_host()
    {
        using var h = new Harness();
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Host.Script = call => { call.Enter(StartupPhase.Starting); return never.Task; };
        h.Host.OnFree = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct); // reason: the host never lets go; only the supervisor's cancellation ends it

        var result = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.FirstStart));

        Assert.Equal(StartOutcome.GaveUp, result.Outcome);
    }

    // ------------------------------------------------------------------ the limit, the window, the reset

    [Fact]
    public async Task A_start_that_always_fails_is_tried_up_to_the_limit_then_given_up_in_words()
    {
        using var h = new Harness();
        h.Host.Script = _ => throw new InvalidOperationException("port in use");

        var result = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.FirstStart));

        Assert.Equal(StartOutcome.GaveUp, result.Outcome);
        Assert.Equal(5, h.Host.Starts);
        Assert.Equal(5, result.Attempts);
        Assert.StartsWith("gave up 5", h.Events[^1]);
        Assert.Matches(new Regex(@"ERROR .*5 starts failed within 15 minutes.*Restart.*port in use"), h.Log);
        Assert.DoesNotContain(h.Events, e => e.StartsWith("recovered"));
        for (var n = 1; n <= 5; n++)
        {
            Assert.Contains($"try {n} of 5", h.Log);
        }

        // Having given up, it does not try again on its own...
        var again = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.UnexpectedStop), limit: TimeSpan.FromMinutes(5));
        Assert.Equal(StartOutcome.GaveUp, again.Outcome);
        Assert.Equal(5, h.Host.Starts);
    }

    [Fact]
    public async Task Choosing_Restart_after_giving_up_starts_the_count_over()
    {
        using var h = new Harness();
        var fail = true;
        h.Host.Script = call =>
        {
            if (fail)
            {
                throw new InvalidOperationException("no");
            }

            call.Enter(StartupPhase.Ready);
            return Task.CompletedTask;
        };
        await h.DriveAsync(h.Supervisor.StartAsync(StartReason.FirstStart));
        Assert.StartsWith("gave up", h.Events[^1]);

        fail = false;
        h.Supervisor.Cancel();
        var result = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.Restart));

        Assert.Equal(StartOutcome.Started, result.Outcome);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(6, h.Host.Starts);
    }

    [Fact]
    public async Task Failures_older_than_the_window_do_not_count_toward_the_limit()
    {
        using var h = new Harness();
        h.Host.Script = _ => throw new InvalidOperationException("no");

        // Four failed starts, one at a time, six minutes apart: never five inside fifteen minutes.
        for (var i = 0; i < 4; i++)
        {
            var one = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.PortChange, retry: false));
            Assert.Equal(StartOutcome.Failed, one.Outcome);
            await h.PassAsync(TimeSpan.FromMinutes(6), TimeSpan.FromSeconds(30));
        }

        // The first has long left the 15 minute window: a fifth failure, at 24 minutes, is only the third inside it (12, 18 and 24).
        var fifth = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.PortChange, retry: false));
        Assert.Equal(StartOutcome.Failed, fifth.Outcome);
        Assert.Equal(3, h.Supervisor.FailuresInWindow);
        Assert.DoesNotContain(h.Events, e => e.StartsWith("gave up"));
    }

    [Fact]
    public async Task Failures_inside_the_window_do_reach_the_limit()
    {
        using var h = new Harness();
        h.Host.Script = _ => throw new InvalidOperationException("no");

        StartResult last = null!;
        for (var i = 0; i < 5; i++)
        {
            last = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.PortChange, retry: false));
            await h.PassAsync(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30));
        }

        Assert.Equal(5, h.Supervisor.FailuresInWindow);
        var sixth = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.PortChange, retry: false));
        Assert.Equal(StartOutcome.GaveUp, sixth.Outcome);
        Assert.Equal(5, h.Host.Starts);
    }

    [Fact]
    public async Task A_host_that_runs_steadily_clears_the_count_by_time_without_waiting_for_the_next_stop()
    {
        using var h = new Harness();
        h.Host.Script = call =>
        {
            if (call.Attempt <= 2)
            {
                throw new InvalidOperationException("no");
            }

            call.Enter(StartupPhase.Ready);
            return Task.CompletedTask;
        };
        await h.DriveAsync(h.Supervisor.StartAsync(StartReason.FirstStart));
        Assert.Equal(2, h.Supervisor.FailuresInWindow);

        // It stays up. No stop happens; the clock alone reaches "steady".
        await h.PassAsync(h.Supervisor.Settings.StableAfter + TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
        await h.SettleAsync();

        Assert.Equal(0, h.Supervisor.FailuresInWindow);
        Assert.Contains("stable", h.Events);
        Assert.Matches(new Regex(@"INFO .*run steadily for 10 minutes"), h.Log);
    }

    [Fact]
    public async Task A_host_that_stops_before_it_was_steady_keeps_its_count()
    {
        using var h = new Harness();
        h.Host.Script = call => { call.Enter(StartupPhase.Ready); return Task.CompletedTask; };
        await h.DriveAsync(h.Supervisor.StartAsync(StartReason.FirstStart));
        await h.PassAsync(TimeSpan.FromMinutes(4), TimeSpan.FromSeconds(30));

        var result = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.UnexpectedStop));

        Assert.Equal(StartOutcome.Started, result.Outcome);
        Assert.Equal(1, h.Supervisor.FailuresInWindow);
        Assert.DoesNotContain("stable", h.Events);
    }

    // ------------------------------------------------------------------ backoff and jitter

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public async Task The_wait_grows_each_time_stays_in_its_bounds_and_is_capped(double random)
    {
        using var h = new Harness(new HostSupervisorSettings { StartLimit = 9, FailureWindow = TimeSpan.FromHours(3) }, random: random);
        h.Host.Script = _ => throw new InvalidOperationException("no");

        await h.DriveAsync(h.Supervisor.StartAsync(StartReason.FirstStart));

        Assert.Equal(9, h.Host.Starts);
        var s = h.Supervisor.Settings;
        for (var failures = 1; failures <= 8; failures++)
        {
            var upper = TimeSpan.FromTicks(Math.Min(s.MaxBackoff.Ticks, s.BaseBackoff.Ticks * (1L << (failures - 1))));
            var lower = TimeSpan.FromTicks((long)(upper.Ticks * (1 - s.JitterFraction)));
            var delay = h.Attempts[failures].Delay;
            Assert.InRange(delay, lower - TimeSpan.FromMilliseconds(1), upper + TimeSpan.FromMilliseconds(1));
            Assert.True(delay <= s.MaxBackoff);
        }

        // The cap is reached: 15, 30, 60, 120, 240, then 300 for as long as it goes on.
        Assert.Equal(TimeSpan.FromMinutes(5).TotalSeconds * (random == 0 ? 0.5 : random == 0.5 ? 0.75 : 1.0), h.Attempts[8].Delay.TotalSeconds, precision: 0);
    }

    [Fact]
    public async Task Different_random_draws_give_different_waits_so_two_apps_do_not_retry_together()
    {
        using var low = new Harness(random: 0.0);
        using var high = new Harness(random: 1.0);
        low.Host.Script = call => call.Attempt == 1 ? throw new InvalidOperationException("no") : Task.CompletedTask;
        high.Host.Script = call => call.Attempt == 1 ? throw new InvalidOperationException("no") : Task.CompletedTask;

        await low.DriveAsync(low.Supervisor.StartAsync(StartReason.FirstStart));
        await high.DriveAsync(high.Supervisor.StartAsync(StartReason.FirstStart));

        Assert.True(low.Attempts[1].Delay < high.Attempts[1].Delay);
    }

    // ------------------------------------------------------------------ other start paths

    [Fact]
    public async Task A_stop_nobody_asked_for_counts_as_a_failure_and_waits_before_starting_again()
    {
        using var h = new Harness();
        h.Host.Script = call => { call.Enter(StartupPhase.Ready); return Task.CompletedTask; };

        var result = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.UnexpectedStop));

        Assert.Equal(StartOutcome.Started, result.Outcome);
        Assert.Equal(1, h.Supervisor.FailuresInWindow);
        Assert.True(h.Attempts[0].Delay >= TimeSpan.FromSeconds(7.5));
        Assert.Matches(new Regex(@"after it stopped by itself.*waiting \d+ seconds"), h.Log);
    }

    [Fact]
    public async Task Five_stops_nobody_asked_for_inside_the_window_end_in_giving_up()
    {
        using var h = new Harness();
        h.Host.Script = call => { call.Enter(StartupPhase.Ready); return Task.CompletedTask; };

        StartResult last = null!;
        for (var i = 0; i < 6; i++)
        {
            last = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.UnexpectedStop));
            if (last.Outcome == StartOutcome.GaveUp)
            {
                break;
            }

            await h.PassAsync(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5));
        }

        Assert.Equal(StartOutcome.GaveUp, last.Outcome);
        Assert.Equal(4, h.Host.Starts);
    }

    [Fact]
    public async Task A_port_change_start_is_one_try_with_no_wait_and_reports_the_failure()
    {
        using var h = new Harness();
        h.Host.Script = _ => throw new InvalidOperationException("address already in use");

        var result = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.PortChange, retry: false));

        Assert.Equal(StartOutcome.Failed, result.Outcome);
        Assert.Equal(1, h.Host.Starts);
        Assert.Equal("address already in use", result.Exception!.Message);
        Assert.Equal(TimeSpan.Zero, h.Attempts[0].Delay);
    }

    [Fact]
    public async Task A_port_change_start_that_hangs_is_cut_off_too_and_reported_as_a_failure()
    {
        using var h = new Harness();
        h.Host.Script = async call =>
        {
            call.Enter(StartupPhase.WebHost);
            await call.HangAsync();
        };

        var result = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.PortChange, retry: false));

        Assert.Equal(StartOutcome.Failed, result.Outcome);
        Assert.Contains("no progress", result.Failure);
        Assert.Equal(1, h.Host.Starts);
    }

    // ------------------------------------------------------------------ choosing Stop, Restart or Quit

    [Fact]
    public async Task Choosing_Stop_or_Restart_while_waiting_between_tries_ends_the_wait()
    {
        using var h = new Harness();
        h.Host.Script = _ => throw new InvalidOperationException("no");

        var running = h.Supervisor.StartAsync(StartReason.FirstStart);
        await h.SettleAsync();
        await h.PassAsync(TimeSpan.FromSeconds(2));
        h.Supervisor.Cancel();
        var result = await h.DriveAsync(running);

        Assert.Equal(StartOutcome.Cancelled, result.Outcome);
        Assert.Equal(1, h.Host.Starts);
    }

    [Fact]
    public async Task Choosing_Stop_while_a_start_is_in_flight_cancels_it_and_stops_what_it_made()
    {
        using var h = new Harness();
        h.Host.Script = async call =>
        {
            call.Enter(StartupPhase.Migrations, "platform: V0001");
            await call.HangAsync();
        };

        var running = h.Supervisor.StartAsync(StartReason.FirstStart);
        await h.SettleAsync();
        await h.PassAsync(TimeSpan.FromSeconds(5));
        h.Supervisor.Cancel();
        var result = await h.DriveAsync(running);

        Assert.Equal(StartOutcome.Cancelled, result.Outcome);
        Assert.Single(h.Host.CancelTimes);
        Assert.True(h.Host.Stops + h.Host.Frees >= 1, "what the cancelled start had made must be stopped");
        Assert.DoesNotContain(h.Events, e => e.StartsWith("recovered"));
    }

    [Fact]
    public async Task A_start_that_finishes_just_as_Stop_is_chosen_is_stopped_again()
    {
        using var h = new Harness();
        h.Host.Script = call =>
        {
            h.Supervisor.Cancel();
            call.Enter(StartupPhase.Ready);
            return Task.CompletedTask;
        };

        var result = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.FirstStart));

        Assert.Equal(StartOutcome.Cancelled, result.Outcome);
        Assert.Equal(1, h.Host.Stops);
        Assert.DoesNotContain(h.Events, e => e.StartsWith("recovered"));
    }

    // ------------------------------------------------------------------ progress and markers during supervised starts

    [Fact]
    public async Task A_stale_marker_from_another_process_does_not_make_a_new_start_look_hung_or_progressed()
    {
        using var h = new Harness();
        // A previous process died in the middle of a migration an hour ago and left its marker.
        StartMarkerFile.Write(h.MarkerPath, new StartMarker(999, 3, "migrations", "platform: V0009", Harness.Epoch - TimeSpan.FromHours(1)));
        var progress = new StartProgress(h.Time, h.MarkerPath, 4242, line => h.Lines.Add("progress: " + line));
        var host = new ScriptedHost { Progress = progress, Time = h.Time };
        host.Script = async call =>
        {
            call.Enter(StartupPhase.OpeningDatabases, "games");
            await call.WorkAsync(TimeSpan.FromMinutes(3));
            call.Enter(StartupPhase.Ready);
        };
        var supervisor = new HostSupervisor(host, progress, new HostRestartReporter(), new RecordingLog(h.Lines), h.Time, restarter: null, diagnostics: null, random: () => 0.5, runOffThread: Harness.InlineRun);

        var result = await h.DriveAsync(supervisor.StartAsync(StartReason.FirstStart));

        Assert.Equal(StartOutcome.Started, result.Outcome);
        Assert.Equal(1, host.Starts);
        Assert.Empty(host.CancelTimes);
        Assert.NotNull(progress.PreviousRun);
        Assert.Equal(999, progress.PreviousRun!.Pid);
        Assert.Contains("previous", h.Log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_half_written_marker_is_ignored_and_replaced()
    {
        using var h = new Harness();
        File.WriteAllText(h.MarkerPath, "{\"pid\":4242,\"attempt\":1,\"pha");
        var progress = new StartProgress(h.Time, h.MarkerPath, 4242);
        var host = new ScriptedHost { Progress = progress, Time = h.Time };
        host.Script = call => { call.Enter(StartupPhase.Ready); return Task.CompletedTask; };
        var supervisor = new HostSupervisor(host, progress, new HostRestartReporter(), new RecordingLog(h.Lines), h.Time, random: () => 0.5, runOffThread: Harness.InlineRun);

        var result = await h.DriveAsync(supervisor.StartAsync(StartReason.FirstStart));

        Assert.Equal(StartOutcome.Started, result.Outcome);
        var read = StartMarkerFile.Read(h.MarkerPath, 4242);
        Assert.Equal(StartMarkerKind.Own, read.Kind);
        Assert.Equal("ready", read.Marker!.Phase);
    }

    [Fact]
    public async Task Each_attempt_starts_again_from_the_first_phase_so_an_old_phase_never_carries_over()
    {
        using var h = new Harness();
        var firstPhaseOfSecond = StartupPhase.Ready;
        h.Host.Script = async call =>
        {
            if (call.Attempt == 1)
            {
                call.Enter(StartupPhase.Migrations, "platform");
                await call.HangAsync();
                return;
            }

            firstPhaseOfSecond = call.Progress.Current.Phase;
            call.Enter(StartupPhase.Ready);
        };

        await h.DriveAsync(h.Supervisor.StartAsync(StartReason.FirstStart));

        Assert.True(firstPhaseOfSecond == StartupPhase.Starting, firstPhaseOfSecond + " " + h.Log);
        Assert.Equal(2, h.Progress.Current.Attempt);
    }

    // ------------------------------------------------------------------ stress

    // ------------------------------------------------------------------ races found by reading the code (#1256)

    /// <summary>A fake clock that keeps the timers it was asked for, so a test can run a callback "late", as the real clock can.</summary>
    private sealed class CapturingTime(DateTimeOffset start) : FakeTimeProvider(start)
    {
        public List<(TimerCallback Callback, object? State, TimeSpan Due)> Timers { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (Timers)
            {
                Timers.Add((callback, state, dueTime));
            }

            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    [Fact]
    public async Task A_steady_timer_that_fired_just_before_a_failure_does_not_clear_that_failure()
    {
        var time = new CapturingTime(Harness.Epoch);
        using var h = new Harness(time: time);
        h.Host.Script = call =>
        {
            if (call.Attempt > 1)
            {
                throw new InvalidOperationException("no");
            }

            call.Enter(StartupPhase.Ready);
            return Task.CompletedTask;
        };
        Assert.Equal(StartOutcome.Started, (await h.DriveAsync(h.Supervisor.StartAsync(StartReason.FirstStart))).Outcome);
        var steady = time.Timers.Last(t => t.Due == h.Supervisor.Settings.StableAfter);

        // The host stops by itself. While the supervisor waits to try again, the steady timer's callback - which had already
        // fired on another thread and was only waiting for the lock - gets its turn.
        var second = h.Supervisor.StartAsync(StartReason.UnexpectedStop);
        await h.PassAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, h.Supervisor.FailuresInWindow);
        steady.Callback(steady.State);

        Assert.Equal(1, h.Supervisor.FailuresInWindow);
        Assert.DoesNotContain("stable", h.Events);
        Assert.DoesNotContain("run steadily", h.Log);
        h.Supervisor.Cancel();
        await h.DriveAsync(second);
    }

    [Fact]
    public async Task Choosing_Stop_while_a_hung_try_is_being_freed_leaves_no_failure_behind()
    {
        using var h = new Harness();
        var freeing = new TaskCompletionSource();
        h.Host.Script = async call =>
        {
            call.Enter(StartupPhase.Migrations, "platform: V0001");
            await call.HangAsync();
        };
        h.Host.OnFree = _ => freeing.Task;

        var running = h.Supervisor.StartAsync(StartReason.FirstStart);
        var passed = TimeSpan.Zero;
        while (h.Host.Frees == 0)
        {
            Assert.True(passed < TimeSpan.FromHours(1), "the hung try was never released");
            h.AdvanceInline(TimeSpan.FromSeconds(5));
            passed += TimeSpan.FromSeconds(5);
            await Task.Yield();
        }

        // The person chooses Restart (which starts the count over) just as the hung try is judged a failure.
        h.Supervisor.Cancel();
        freeing.SetResult();
        var result = await h.DriveAsync(running);

        Assert.Equal(StartOutcome.Cancelled, result.Outcome);
        Assert.Equal(0, h.Supervisor.FailuresInWindow);
        Assert.DoesNotContain(h.Events, e => e.StartsWith("failed"));
    }

    [Fact]
    public async Task A_start_that_reports_ready_and_then_does_not_return_is_a_failed_try_not_a_quiet_cancel()
    {
        using var h = new Harness();
        h.Host.Script = async call =>
        {
            call.Enter(StartupPhase.WebHost, "binding the port");
            await call.WorkAsync(TimeSpan.FromSeconds(5));
            call.Enter(StartupPhase.Ready);
            await call.HangAsync(); // the tail of the start never returns
        };

        var result = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.PortChange, retry: false));

        Assert.Equal(StartOutcome.Failed, result.Outcome);
        Assert.Contains("reported ready", result.Failure);
        Assert.Equal(1, h.Host.Frees);
    }

    [Fact]
    public async Task Stress_forty_runs_of_the_loop_with_mixed_failures_hangs_and_slow_steps_always_end_well()
    {
        // DELUNO_STRESS_SEEDS raises the count for a hunt (for example 2000); the default is the 40 the suite runs.
        var seeds = int.TryParse(Environment.GetEnvironmentVariable("DELUNO_STRESS_SEEDS"), out var asked) && asked > 0 ? asked : 40;
        var repeats = int.TryParse(Environment.GetEnvironmentVariable("DELUNO_STRESS_REPEAT"), out var times) && times > 0 ? times : 1;
        // DELUNO_STRESS_STARVE=1 fills the thread pool with blocked work, so any continuation the supervisor leaves to the pool
        // arrives long after the fake clock has moved on: the harshest way to show a race between the clock and the pool.
        using var release = new ManualResetEventSlim();
        if (Environment.GetEnvironmentVariable("DELUNO_STRESS_STARVE") == "1")
        {
            ThreadPool.GetMinThreads(out var worker, out var io);
            ThreadPool.SetMinThreads(Math.Min(worker, 2), io);
            for (var i = 0; i < Environment.ProcessorCount * 8; i++)
            {
                _ = Task.Run(() => release.Wait(TimeSpan.FromMinutes(5)));
            }
        }

        try
        {
            for (var round = 1; round <= repeats; round++)
            {
                for (var seed = 1; seed <= seeds; seed++)
                {
                    await RunStressSeedAsync(seed);
                }
            }
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>
    /// One seeded run. A failure reports the run, the assertion, the plan, the supervisor's own log and state, the host's
    /// counters and the fake clock, so a single failure explains itself.
    /// </summary>
    private static async Task RunStressSeedAsync(int seed)
    {
        var rng = new Random(seed);
        using var h = new Harness(random: rng.NextDouble());
        var plan = Enumerable.Range(0, rng.Next(0, 7)).Select(_ =>
        {
            var kind = rng.Next(4) switch { 0 => "fail", 1 => "hang", 2 => "slow", _ => "instant" };
            return (kind, AllPhases[rng.Next(AllPhases.Length)]);
        }).ToList();
        h.Host.Script = async call =>
        {
            if (call.Attempt > plan.Count)
            {
                call.Enter(StartupPhase.Ready);
                return;
            }

            var (kind, phase) = plan[call.Attempt - 1];
            call.Enter(phase, "step " + call.Attempt);
            switch (kind)
            {
                case "fail":
                    throw new InvalidOperationException("failed in " + phase);
                case "hang":
                    await call.HangAsync();
                    return;
                case "slow":
                    for (var i = 0; i < 20; i++)
                    {
                        call.Enter(phase, $"step {call.Attempt}.{i}");
                        await call.WorkAsync(TimeSpan.FromMinutes(1));
                    }

                    call.Enter(StartupPhase.Ready);
                    return;
                default:
                    call.Enter(StartupPhase.Ready);
                    return;
            }
        };

        StartResult? result = null;
        string where = "before the run ended";
        try
        {
            result = await h.DriveAsync(h.Supervisor.StartAsync(StartReason.FirstStart), step: TimeSpan.FromSeconds(5));

            // It always ends: started, or gave up after the limit; never a half-way state.
            where = "the outcome is Started or GaveUp";
            Assert.True(result.Outcome is StartOutcome.Started or StartOutcome.GaveUp, result.Outcome.ToString());
            // The limit is on failures inside the window, not on starts: tries that each take minutes (a hang is cut off after its
            // whole window) let the early failures age out, so more than five starts in all is correct then. Inside one window
            // it is five at most.
            where = "starts are within the limit while the whole run fits one failure window";
            if (h.Time.GetUtcNow() - Harness.Epoch < h.Supervisor.Settings.FailureWindow)
            {
                Assert.True(h.Host.Starts <= h.Supervisor.Settings.StartLimit, $"{h.Host.Starts} starts");
            }

            where = "failures in the window are within the limit";
            Assert.True(h.Supervisor.FailuresInWindow <= h.Supervisor.Settings.StartLimit, h.Supervisor.FailuresInWindow.ToString());
            // Every attempt is numbered in the log, with its reason (a number counts the failures still inside the window, so a
            // slow try that outlives the window can make it lower than the count of starts); every wait is inside its bounds.
            where = "every start has a numbered line in the log";
            Assert.Equal(h.Host.Starts, Regex.Matches(h.Log, @"Start try \d+ of 5 \(first start\)").Count);

            where = "every retry waited within its bounds";
            foreach (var attempt in h.Attempts.Skip(1))
            {
                Assert.InRange(attempt.Delay, TimeSpan.Zero, h.Supervisor.Settings.MaxBackoff);
                Assert.True(attempt.Delay >= TimeSpan.FromSeconds(7.4), "a retry with no wait");
            }

            where = "a start that needed a retry is a warning";
            if (result.Outcome == StartOutcome.Started && h.Host.Starts > 1)
            {
                Assert.Contains("WARN", h.Log);
            }

            // The host is freed after every failed or hung try, and never twice for one try.
            where = "the host is freed at most once per start";
            Assert.True(h.Host.Frees <= h.Host.Starts, $"{h.Host.Frees} frees, {h.Host.Starts} starts");
            where = "the host is freed once for each start that did not succeed";
            Assert.Equal(h.Host.Starts - (result.Outcome == StartOutcome.Started ? 1 : 0), h.Host.Frees);
        }
        catch (Exception ex)
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine($"STRESS run (seed) {seed} failed at: {where}");
            report.AppendLine($"  assertion: {ex.GetType().Name}: {ex.Message.Replace("\n", " | ")}");
            report.AppendLine($"  plan (one entry per attempt): {string.Join(", ", plan.Select(p => p.kind + "@" + p.Item2))}");
            report.AppendLine($"  outcome: {(result is null ? "(none)" : result.Outcome + " after " + result.Attempts + " attempts, failure: " + result.Failure)}");
            report.AppendLine($"  fake clock: +{(h.Time.GetUtcNow() - Harness.Epoch).TotalSeconds:0}s");
            report.AppendLine($"  supervisor: failuresInWindow={h.Supervisor.FailuresInWindow}");
            report.AppendLine($"  host: starts={h.Host.Starts} stops={h.Host.Stops} frees={h.Host.Frees} cancels={h.Host.CancelTimes.Count}");
            report.AppendLine($"  progress: {h.Progress.Current}");
            report.AppendLine($"  threadpool: threads={ThreadPool.ThreadCount} pending={ThreadPool.PendingWorkItemCount}");
            report.AppendLine($"  events: {string.Join(" / ", h.Events)}");
            report.AppendLine("  supervisor log:");
            report.AppendLine(h.Log);
            throw new Xunit.Sdk.XunitException("STRESS " + report);
        }
    }
}
