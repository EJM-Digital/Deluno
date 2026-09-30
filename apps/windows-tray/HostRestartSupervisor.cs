namespace Deluno.Tray;

/// <summary>
/// Starts Deluno's web host again when it stopped without being asked to (#1080).
///
/// <para>In tray mode the tray icon is a separate thing from the web host inside
/// the same process. When a critical background service cannot be kept running
/// the host stops itself, and without this the icon stays and Deluno is simply
/// gone. This waits a little, starts the host again, and tries again with a
/// longer wait each time, up to <see cref="MaxAttempts"/> tries in a row. A host
/// that then stays up for <see cref="StableAfter"/> is trusted again and the
/// count starts over. When it cannot be started it says so in plain words,
/// through <see cref="Reporter"/>, and stops trying until the person chooses
/// Restart.</para>
/// </summary>
public sealed class HostRestartSupervisor(
    Func<CancellationToken, Task> startHost,
    HostRestartReporter reporter,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<DateTimeOffset>? now = null,
    Func<Task>? stopHost = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((span, token) => Task.Delay(span, token));
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly object _gate = new();
    private CancellationTokenSource _cts = new();
    private int _attempts;
    private DateTimeOffset _lastStartedUtc = DateTimeOffset.MinValue;
    private int _running;

    public HostRestartReporter Reporter => reporter;

    /// <summary>Tries in a row before the supervisor gives up.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>The wait before the first try; it doubles each time up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>A host that then runs this long starts the count over.</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Call after any successful start, including one made by hand, so "stable" is measured from it.</summary>
    public void NoteStarted()
    {
        lock (_gate)
        {
            _lastStartedUtc = _now();
        }
    }

    /// <summary>The person chose Stop, Restart or Quit: forget any restart in progress and start counting fresh.</summary>
    public void Cancel()
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            old = _cts;
            _cts = new CancellationTokenSource();
            _attempts = 0;
        }

        try
        {
            old.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>The host stopped and nobody asked it to.</summary>
    public async Task HandleUnexpectedStopAsync()
    {
        // One restart loop at a time.
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        CancellationToken token;
        try
        {
            lock (_gate)
            {
                token = _cts.Token;
                if (_lastStartedUtc != DateTimeOffset.MinValue && _now() - _lastStartedUtc >= StableAfter)
                {
                    _attempts = 0;
                }
            }

            while (true)
            {
                int attempt;
                lock (_gate)
                {
                    if (_attempts >= MaxAttempts)
                    {
                        break;
                    }

                    attempt = ++_attempts;
                }

                var pause = TimeSpan.FromTicks(Math.Min(
                    MaxBackoff.Ticks,
                    BaseBackoff.Ticks * (1L << Math.Min(attempt - 1, 10))));
                reporter.Restarting(attempt, MaxAttempts, pause);

                try
                {
                    await _delay(pause, token).ConfigureAwait(false);
                    await startHost(token).ConfigureAwait(false);
                    if (token.IsCancellationRequested)
                    {
                        // The person chose Stop, Restart or Quit while the start was in flight, and
                        // their stop found nothing to stop yet. Stop the host this start just made.
                        if (stopHost is not null)
                        {
                            await stopHost().ConfigureAwait(false);
                        }

                        return;
                    }

                    NoteStarted();
                    reporter.Recovered();
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception failure)
                {
                    reporter.AttemptFailed(attempt, failure);
                }
            }

            reporter.GaveUp(MaxAttempts);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}

/// <summary>What the supervisor tells the person, in plain words. The tray shows these on its icon.</summary>
public sealed class HostRestartReporter
{
    public Action<int, int, TimeSpan> Restarting { get; init; } = static (_, _, _) => { };
    public Action Recovered { get; init; } = static () => { };
    public Action<int, Exception> AttemptFailed { get; init; } = static (_, _) => { };
    public Action<int> GaveUp { get; init; } = static _ => { };

    /// <summary>"Deluno stopped. Starting it again in 15 seconds (try 1 of 5)."</summary>
    public static string RestartingMessage(int attempt, int max, TimeSpan pause)
        => $"Deluno stopped by itself. Starting it again in {Math.Max(1, (int)Math.Round(pause.TotalSeconds))} seconds (try {attempt} of {max}).";

    public static string GaveUpMessage(int max)
        => $"Deluno stopped by itself and could not be started again after {max} tries. Choose Restart from the Deluno icon menu. The reason is in %LocalAppData%\\Deluno\\logs.";
}
