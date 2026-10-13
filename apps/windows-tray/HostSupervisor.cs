using System.Globalization;
using Deluno.Infrastructure.Storage;

namespace Deluno.Tray;

/// <summary>Why the host is being started (#1256). All of them go through the one <see cref="HostSupervisor"/>.</summary>
public enum StartReason
{
    /// <summary>Deluno was just opened: a normal start, the first run after Setup, the first start after an update, or a pending update applied at start.</summary>
    FirstStart,

    /// <summary>The person chose Restart (or an action that restarts, such as letting any address in).</summary>
    Restart,

    /// <summary>The person chose a new port.</summary>
    PortChange,

    /// <summary>The host stopped and nobody asked it to.</summary>
    UnexpectedStop
}

public enum StartOutcome
{
    /// <summary>The host is listening.</summary>
    Started,

    /// <summary>One try failed and no retry was asked for (a port change rolls back instead).</summary>
    Failed,

    /// <summary>The start limit was reached, or a hung host could not be freed or restarted: stays down until Restart.</summary>
    GaveUp,

    /// <summary>The person chose Stop, Restart or Quit while this was going on.</summary>
    Cancelled,

    /// <summary>The host could not be freed, so the process was started again: this one must exit.</summary>
    ProcessRestarting
}

public sealed record StartResult(StartOutcome Outcome, int Attempts, string? Failure, Exception? Exception);

/// <summary>The web host as the supervisor sees it.</summary>
public interface IStartableHost
{
    /// <summary>Builds and starts the host. Throws when it cannot; honours the token where it can.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>A normal stop.</summary>
    Task StopAsync();

    /// <summary>Stops and disposes whatever a failed, hung or cancelled start left, so its databases and port are free. Bounded by the token.</summary>
    Task FreeAsync(CancellationToken cancellationToken);
}

/// <summary>Starts the program over when the in-process host cannot be freed. False when it must not (a loop guard) or could not.</summary>
public interface IProcessRestarter
{
    bool TryRestart(string reason);
}

public interface ISupervisorLog
{
    void Info(string message);
    void Warning(string message);
    void Error(string message);
}

/// <summary>
/// The numbers behind the supervisor, modelled on systemd's Restart and StartLimit, Windows service recovery and the
/// startup/liveness probes of Kubernetes (#1256).
/// </summary>
public sealed class HostSupervisorSettings
{
    /// <summary>Failed starts inside <see cref="FailureWindow"/> before it gives up (systemd StartLimitBurst).</summary>
    public int StartLimit { get; init; } = 5;

    /// <summary>The window those failures are counted in (systemd StartLimitIntervalSec).</summary>
    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The wait after the first failure; it doubles for each further one up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How much of each wait is randomised: the wait is between (1 - this) and 1 times the backoff, so two apps that
    /// failed together do not try again together.
    /// </summary>
    public double JitterFraction { get; init; } = 0.5;

    /// <summary>A host that then runs this long clears the count of failed starts, by the clock (not only at the next stop).</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>After a start is judged hung or cancelled: how long it gets to end by itself before the host is stopped and disposed.</summary>
    public TimeSpan CancelBudget { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long stopping and disposing the host may take, and then how long the start gets to let go after that.</summary>
    public TimeSpan FreeBudget { get; init; } = TimeSpan.FromSeconds(30);

    public StartWindows Windows { get; init; } = new();
}

/// <summary>What one try is, for the log and the tray.</summary>
public sealed record StartAttemptInfo(StartReason Reason, int Attempt, int Limit, TimeSpan Delay);

/// <summary>What the supervisor tells the person, through the tray. The log is written by the supervisor itself.</summary>
public sealed class HostRestartReporter
{
    /// <summary>A try is about to happen (after its wait).</summary>
    public Action<StartAttemptInfo> Attempting { get; init; } = static _ => { };

    /// <summary>A try failed or hung; the text says why.</summary>
    public Action<StartAttemptInfo, string> AttemptFailed { get; init; } = static (_, _) => { };

    /// <summary>The host is listening; the number is the try that did it.</summary>
    public Action<StartReason, int> Recovered { get; init; } = static (_, _) => { };

    /// <summary>Failures in the window, the window, and the last problem. It stays down until the person chooses Restart.</summary>
    public Action<int, TimeSpan, string> GaveUp { get; init; } = static (_, _, _) => { };

    /// <summary>The host ran long enough to be trusted again.</summary>
    public Action Stable { get; init; } = static () => { };

    /// <summary>The host could not be freed, so the program is starting over.</summary>
    public Action<string> ProcessRestarting { get; init; } = static _ => { };

    /// <summary>"Deluno stopped by itself. Starting it again in 15 seconds (try 2 of 5)."</summary>
    public static string RetryingMessage(StartAttemptInfo info)
    {
        var seconds = Math.Max(1, (int)Math.Round(info.Delay.TotalSeconds));
        return info.Reason == StartReason.UnexpectedStop
            ? $"Deluno stopped by itself. Starting it again in {seconds} seconds (try {info.Attempt} of {info.Limit})."
            : $"Deluno did not start. Trying again in {seconds} seconds (try {info.Attempt} of {info.Limit}).";
    }

    /// <summary>The words when the supervisor gives up. The click on the balloon is Restart.</summary>
    public static string GaveUpMessage(int failures, TimeSpan window, string last)
        => $"Deluno could not be started after {failures} tries in {Math.Round(window.TotalMinutes):0} minutes: {last} Click here to try again. The reason is in %LocalAppData%\\Deluno\\logs.";
}

/// <summary>
/// Starts the web host, every time (#1256): the first start (a normal start, the first run after Setup, the first start
/// after an update, a pending update applied at start), Restart, Change port, and a stop nobody asked for.
///
/// <para>One try runs off the calling thread, watched against the start-up progress: a start that has reported no
/// progress for its phase's window is hung. A hung or failed try is cancelled, then the host is stopped and disposed so its
/// databases and port are free; if it still cannot be freed the process starts over (and a loop guard stops that from
/// repeating). A failed try waits a capped, growing, jittered time and tries again, until it has failed
/// <see cref="HostSupervisorSettings.StartLimit"/> times inside <see cref="HostSupervisorSettings.FailureWindow"/>; then it
/// gives up until the person chooses Restart. A host that then runs for <see cref="HostSupervisorSettings.StableAfter"/>
/// clears the count, by the clock. Every try is logged with its number, reason and next wait, and a start that only worked
/// on a retry is a warning.</para>
/// </summary>
public sealed class HostSupervisor
{
    private readonly IStartableHost _host;
    private readonly IStartProgressSource _progress;
    private readonly HostRestartReporter _reporter;
    private readonly ISupervisorLog _log;
    private readonly TimeProvider _time;
    private readonly IProcessRestarter? _restarter;
    private readonly Func<string>? _diagnostics;
    private readonly Func<double> _random;
    private readonly Func<Func<Task>, Task> _runOffThread;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly List<(DateTimeOffset At, string Why)> _failures = [];
    private CancellationTokenSource _cts = new();
    private DateTimeOffset? _lastStartedUtc;
    private ITimer? _stableTimer;
    private long _generation;

    public HostSupervisor(
        IStartableHost host,
        IStartProgressSource progress,
        HostRestartReporter reporter,
        ISupervisorLog log,
        TimeProvider time,
        HostSupervisorSettings? settings = null,
        IProcessRestarter? restarter = null,
        Func<string>? diagnostics = null,
        Func<double>? random = null,
        Func<Func<Task>, Task>? runOffThread = null)
    {
        // Where a try runs. The thread pool, always, outside tests: a test may run it inline to keep its fake clock exact.
        _runOffThread = runOffThread ?? (work => Task.Run(work));
        _host = host;
        _progress = progress;
        _reporter = reporter;
        _log = log;
        _time = time;
        Settings = settings ?? new HostSupervisorSettings();
        _restarter = restarter;
        _diagnostics = diagnostics;
        _random = random ?? Random.Shared.NextDouble;
    }

    public HostSupervisorSettings Settings { get; }

    /// <summary>Failed starts (and stops nobody asked for) inside the window, now.</summary>
    public int FailuresInWindow
    {
        get
        {
            lock (_gate)
            {
                Prune();
                return _failures.Count;
            }
        }
    }

    /// <summary>The person chose Stop, Restart or Quit: end any start or wait in progress and start counting fresh.</summary>
    public void Cancel()
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            old = _cts;
            _cts = new CancellationTokenSource();
            _failures.Clear();
            DisposeStableTimer();
            _generation++;
        }

        try
        {
            old.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Starts the host and keeps trying until it is listening, the limit is reached, or the person cancels.
    /// <paramref name="retry"/> false is one try only (Change port rolls back to the old port instead of waiting).
    /// </summary>
    public async Task<StartResult> StartAsync(StartReason reason, bool retry = true)
    {
        CancellationToken token;
        lock (_gate)
        {
            token = _cts.Token;
        }

        if (reason == StartReason.UnexpectedStop)
        {
            // One restart loop at a time: a second stop reported while one is being dealt with is the same event.
            if (!_running.Wait(0))
            {
                return new StartResult(StartOutcome.Cancelled, 0, "a start is already in progress", null);
            }
        }
        else
        {
            try
            {
                await _running.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new StartResult(StartOutcome.Cancelled, 0, null, null);
            }
        }

        try
        {
            return await RunAsync(reason, retry, token).ConfigureAwait(false);
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task<StartResult> RunAsync(StartReason reason, bool retry, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return new StartResult(StartOutcome.Cancelled, 0, null, null);
        }

        lock (_gate)
        {
            DisposeStableTimer();
            _generation++;
        }

        if (reason == StartReason.UnexpectedStop)
        {
            RecordFailure("the host stopped without being asked to");
        }

        var attempts = 0;
        var nextDelay = TimeSpan.Zero;
        var first = true;
        while (true)
        {
            int failures;
            string last;
            lock (_gate)
            {
                Prune();
                failures = _failures.Count;
                last = _failures.Count > 0 ? _failures[^1].Why : "the host did not start";
            }

            if (failures >= Settings.StartLimit)
            {
                return GiveUp(attempts, failures, last);
            }

            var attempt = failures + 1;
            if (first)
            {
                nextDelay = failures == 0 ? TimeSpan.Zero : Backoff(failures);
                first = false;
            }

            var info = new StartAttemptInfo(reason, attempt, Settings.StartLimit, nextDelay);
            _log.Info(nextDelay > TimeSpan.Zero
                ? string.Create(CultureInfo.InvariantCulture, $"Start try {attempt} of {Settings.StartLimit} ({ReasonWords(reason)}): waiting {Seconds(nextDelay)} seconds first.")
                : string.Create(CultureInfo.InvariantCulture, $"Start try {attempt} of {Settings.StartLimit} ({ReasonWords(reason)})."));
            _reporter.Attempting(info);

            if (nextDelay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(nextDelay, _time, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return new StartResult(StartOutcome.Cancelled, attempts, null, null);
                }
            }

            if (token.IsCancellationRequested)
            {
                return new StartResult(StartOutcome.Cancelled, attempts, null, null);
            }

            attempts++;
            _progress.BeginAttempt(attempt);
            var tried = await TryOnceAsync(info, token).ConfigureAwait(false);
            switch (tried.Kind)
            {
                case TryKind.Started:
                    NoteStarted();
                    if (attempt > 1)
                    {
                        // A start that needed a retry is a warning, so a log check catches it.
                        _log.Warning(string.Create(CultureInfo.InvariantCulture, $"Deluno started on try {attempt} of {Settings.StartLimit}, after earlier tries failed."));
                    }
                    else
                    {
                        _log.Info("Deluno started on its first try.");
                    }

                    _reporter.Recovered(reason, attempt);
                    return new StartResult(StartOutcome.Started, attempt, null, null);

                case TryKind.Cancelled:
                    return new StartResult(StartOutcome.Cancelled, attempts, null, null);

                case TryKind.ProcessRestarting:
                    return new StartResult(StartOutcome.ProcessRestarting, attempts, tried.Text, null);

                case TryKind.Unfreeable:
                    return GiveUp(attempts, Math.Max(failures, 1), tried.Text);

                default:
                    if (token.IsCancellationRequested)
                    {
                        // Stop, Restart or Quit was chosen while this try was being judged a failure: it is the person's
                        // choice, not a failed start, and Cancel() has just started the count over.
                        return new StartResult(StartOutcome.Cancelled, attempts, null, null);
                    }

                    RecordFailure(tried.Text);
                    lock (_gate)
                    {
                        Prune();
                        failures = _failures.Count;
                    }

                    if (!retry)
                    {
                        _log.Warning(string.Create(CultureInfo.InvariantCulture, $"Start try {attempt} of {Settings.StartLimit} failed while {tried.Phase}: {tried.Text}. Not trying again (this start is one try)."));
                        _reporter.AttemptFailed(info, tried.Text);
                        return new StartResult(StartOutcome.Failed, attempts, tried.Text, tried.Exception);
                    }

                    if (failures >= Settings.StartLimit)
                    {
                        _log.Warning(string.Create(CultureInfo.InvariantCulture, $"Start try {attempt} of {Settings.StartLimit} failed while {tried.Phase}: {tried.Text}."));
                        _reporter.AttemptFailed(info, tried.Text);
                        if (tried.Exception is not null)
                        {
                            _log.Warning("Details of the failed start: " + tried.Exception);
                        }

                        return GiveUp(attempts, failures, tried.Text);
                    }

                    nextDelay = Backoff(failures);
                    _log.Warning(string.Create(CultureInfo.InvariantCulture, $"Start try {attempt} of {Settings.StartLimit} failed while {tried.Phase}: {tried.Text}. Trying again in {Seconds(nextDelay)} seconds."));
                    if (tried.Exception is not null)
                    {
                        _log.Warning("Details of the failed start: " + tried.Exception);
                    }

                    _reporter.AttemptFailed(info, tried.Text);
                    break;
            }
        }
    }

    private enum TryKind { Started, Failed, Cancelled, Unfreeable, ProcessRestarting }

    private sealed record Tried(TryKind Kind, string Text, string Phase, Exception? Exception);

    private async Task<Tried> TryOnceAsync(StartAttemptInfo info, CancellationToken token)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        // Off the calling thread: the tray's UI thread must stay free, and a start that blocks its own thread (a slow
        // synchronous migration) must not take the supervisor with it.
        var startTask = _runOffThread(() => _host.StartAsync(attemptCts.Token));
        var hangTask = StartHangWatch.WaitForHangAsync(_progress, Settings.Windows, _time, watchCts.Token);

        var finished = await Task.WhenAny(startTask, hangTask).ConfigureAwait(false);
        watchCts.Cancel();

        if (ReferenceEquals(finished, startTask) || startTask.IsCompleted)
        {
            return await CompleteAsync(startTask, token).ConfigureAwait(false);
        }

        var hung = await hangTask.ConfigureAwait(false);
        if (hung is null)
        {
            // Stop, Restart or Quit was chosen while the start was in flight. The start was told (its token is linked);
            // it gets its budget to end, and then the host is released.
            await WaitAsync(startTask, Settings.CancelBudget).ConfigureAwait(false);
            if (startTask.IsCompleted)
            {
                return await CompleteAsync(startTask, token).ConfigureAwait(false);
            }

            if (!token.IsCancellationRequested)
            {
                // Nobody chose anything: the watch stopped because the start reported Ready, and the start did not return.
                // That is a hung tail of the start, to be cut off and tried again like any other hang, never a quiet "cancelled"
                // that would leave the host freed and the tray waiting for ever.
                return await HandleHangAsync(info, _progress.Current, attemptCts, startTask).ConfigureAwait(false);
            }

            await EndAsync(startTask, "the start was cancelled by the person", waitFirst: false).ConfigureAwait(false);
            return new Tried(TryKind.Cancelled, string.Empty, string.Empty, null);
        }

        return await HandleHangAsync(info, hung, attemptCts, startTask).ConfigureAwait(false);
    }

    /// <summary>A start task that has ended: started, failed, or cancelled.</summary>
    private async Task<Tried> CompleteAsync(Task startTask, CancellationToken token)
    {
        try
        {
            await startTask.ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is OperationCanceledException || token.IsCancellationRequested)
        {
            await FreeAsync("the start was cancelled").ConfigureAwait(false);
            return new Tried(TryKind.Cancelled, string.Empty, string.Empty, null);
        }
        catch (Exception failure)
        {
            var phase = StartupPhases.Words(_progress.Current.Phase);
            await FreeAsync("the start failed").ConfigureAwait(false);
            return new Tried(TryKind.Failed, failure.Message, phase, failure);
        }

        if (token.IsCancellationRequested)
        {
            // Stop, Restart or Quit was chosen as the start finished, and their stop found nothing to stop yet.
            try
            {
                await _host.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warning("Stopping the host that a cancelled start had just made failed: " + ex.Message);
            }

            return new Tried(TryKind.Cancelled, string.Empty, string.Empty, null);
        }

        return new Tried(TryKind.Started, string.Empty, string.Empty, null);
    }

    private async Task<Tried> HandleHangAsync(StartAttemptInfo info, StartProgressSnapshot hung, CancellationTokenSource attemptCts, Task startTask)
    {
        var window = Settings.Windows.For(hung.Phase);
        var words = StartupPhases.Words(hung.Phase);
        var detail = string.IsNullOrWhiteSpace(hung.Detail) ? "no step named" : hung.Detail;
        var text = hung.Phase == StartupPhase.Ready
            ? string.Create(CultureInfo.InvariantCulture, $"reported ready but its start did not return within {Minutes(Settings.CancelBudget)} (last step: {detail})")
            : string.Create(CultureInfo.InvariantCulture, $"made no progress for {Minutes(window)} while {words} (last step: {detail})");

        _log.Warning(string.Create(
            CultureInfo.InvariantCulture,
            $"Start try {info.Attempt} of {info.Limit} {text}; it began at {hung.AtUtc:u}. Cancelling it."));
        _log.Warning(string.Create(
            CultureInfo.InvariantCulture,
            $"Where it was stuck: phase={StartupPhases.Key(hung.Phase)}, detail={detail}, try={hung.Attempt}, process={hung.Pid}, since={hung.AtUtc:u}, task={startTask.Status}. {Diagnostics()}"));

        try
        {
            attemptCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        var ended = await EndAsync(startTask, "the start made no progress").ConfigureAwait(false);
        if (!ended)
        {
            var reason = string.Create(
                CultureInfo.InvariantCulture,
                $"the start made no progress for {Minutes(window)} while {words} and could not be stopped");
            _log.Error("Deluno's start could not be freed: it did not end, and stopping and disposing the host did not release it. The program is restarting itself.");
            if (_restarter is not null && _restarter.TryRestart(reason))
            {
                _reporter.ProcessRestarting(reason);
                return new Tried(TryKind.ProcessRestarting, reason, words, null);
            }

            _log.Error("Deluno could not restart the program (it has already done that too often, or could not): giving up until Restart is chosen.");
            return new Tried(TryKind.Unfreeable, $"Deluno {text} and could not be stopped", words, null);
        }

        return new Tried(
            TryKind.Failed,
            $"Deluno {text}",
            words,
            new TimeoutException($"The start {text}."));
    }

    /// <summary>
    /// Gives a cancelled start its budget to end by itself, then stops and disposes the host. True when the start task
    /// has ended (failed, cancelled or even finished) and the host is released; false when it is still running.
    /// </summary>
    private async Task<bool> EndAsync(Task startTask, string why, bool waitFirst = true)
    {
        if (waitFirst && !startTask.IsCompleted)
        {
            await WaitAsync(startTask, Settings.CancelBudget).ConfigureAwait(false);
        }

        await FreeAsync(why).ConfigureAwait(false);
        if (!startTask.IsCompleted)
        {
            await WaitAsync(startTask, Settings.FreeBudget).ConfigureAwait(false);
        }

        return startTask.IsCompleted;
    }

    private async Task WaitAsync(Task task, TimeSpan budget)
    {
        using var stop = new CancellationTokenSource();
        var timeout = Task.Delay(budget, _time, stop.Token);
        await Task.WhenAny(task, timeout).ConfigureAwait(false);
        stop.Cancel();
    }

    /// <summary>Stops and disposes the host within a budget. A host that will not let go does not hold the supervisor.</summary>
    private async Task FreeAsync(string why)
    {
        try
        {
            using var budget = new CancellationTokenSource(Settings.FreeBudget, _time);
            var freeing = _host.FreeAsync(budget.Token);
            await WaitAsync(freeing, Settings.FreeBudget + TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (!freeing.IsCompleted)
            {
                _log.Warning($"Releasing the host ({why}) did not finish within {Seconds(Settings.FreeBudget)} seconds.");
            }
            else if (freeing.IsFaulted)
            {
                _log.Warning($"Releasing the host ({why}) failed: {freeing.Exception?.GetBaseException().Message}");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Releasing the host ({why}) failed: {ex.Message}");
        }
    }

    private string Diagnostics()
    {
        try
        {
            return _diagnostics?.Invoke() ?? string.Empty;
        }
        catch (Exception ex)
        {
            return $"(the process could not be looked at: {ex.Message})";
        }
    }

    private StartResult GiveUp(int attempts, int failures, string last)
    {
        var minutes = Minutes(Settings.FailureWindow);
        _log.Error(string.Create(
            CultureInfo.InvariantCulture,
            $"Deluno could not be started: {failures} starts failed within {minutes}. Giving up until Restart is chosen. Last problem: {last}"));
        _reporter.GaveUp(failures, Settings.FailureWindow, last);
        return new StartResult(StartOutcome.GaveUp, attempts, last, null);
    }

    // ------------------------------------------------------------------ the count

    private void RecordFailure(string why)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            // Belt and braces for the timer: a host that ran long enough before it failed starts the count over.
            if (_lastStartedUtc is { } started && now - started >= Settings.StableAfter)
            {
                _failures.Clear();
            }

            _lastStartedUtc = null;
            DisposeStableTimer();
            _failures.Add((now, why));
        }
    }

    private void Prune()
    {
        var cutoff = _time.GetUtcNow() - Settings.FailureWindow;
        _failures.RemoveAll(failure => failure.At <= cutoff);
    }

    /// <summary>The host is listening: from now the clock decides when it counts as stable.</summary>
    private void NoteStarted()
    {
        lock (_gate)
        {
            _lastStartedUtc = _time.GetUtcNow();
            DisposeStableTimer();
            var generation = ++_generation;
            _stableTimer = _time.CreateTimer(_ => OnStable(generation), null, Settings.StableAfter, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnStable(long generation)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            var cleared = _failures.Count;
            _failures.Clear();
            DisposeStableTimer();
            _log.Info(string.Create(
                CultureInfo.InvariantCulture,
                $"Deluno has run steadily for {Minutes(Settings.StableAfter)}; the count of failed starts ({cleared}) is back to zero."));
        }

        _reporter.Stable();
    }

    private void DisposeStableTimer()
    {
        _stableTimer?.Dispose();
        _stableTimer = null;
    }

    /// <summary>The wait before the next try after <paramref name="failures"/> failures: doubling, capped, then jittered down by up to the jitter fraction.</summary>
    private TimeSpan Backoff(int failures)
    {
        var upper = TimeSpan.FromTicks(Math.Min(
            Settings.MaxBackoff.Ticks,
            Settings.BaseBackoff.Ticks * (1L << Math.Min(Math.Max(failures - 1, 0), 10))));
        var jitter = Math.Clamp(Settings.JitterFraction, 0, 1);
        var draw = Math.Clamp(_random(), 0, 1);
        return TimeSpan.FromTicks((long)(upper.Ticks * (1 - jitter + jitter * draw)));
    }

    private static string ReasonWords(StartReason reason) => reason switch
    {
        StartReason.FirstStart => "first start",
        StartReason.Restart => "restart",
        StartReason.PortChange => "port change",
        StartReason.UnexpectedStop => "after it stopped by itself",
        _ => reason.ToString()
    };

    private static int Seconds(TimeSpan span) => Math.Max(0, (int)Math.Round(span.TotalSeconds));

    private static string Minutes(TimeSpan span)
    {
        if (span.TotalMinutes < 1.5 && span.TotalSeconds % 60 != 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Round(span.TotalSeconds):0} seconds");
        }

        var whole = (int)Math.Round(span.TotalMinutes);
        return string.Create(CultureInfo.InvariantCulture, $"{whole} minute{(whole == 1 ? string.Empty : "s")}");
    }
}
