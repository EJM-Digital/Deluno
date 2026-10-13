#pragma warning disable CS9113 // stub: removed with the implementation
using Deluno.Infrastructure.Storage;

namespace Deluno.Tray;

public enum StartReason { FirstStart, Restart, PortChange, UnexpectedStop }

public enum StartOutcome { Started, Failed, GaveUp, Cancelled, ProcessRestarting }

public sealed record StartResult(StartOutcome Outcome, int Attempts, string? Failure, Exception? Exception);

public interface IStartableHost
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
    Task FreeAsync(CancellationToken cancellationToken);
}

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

public sealed class HostSupervisorSettings
{
    public int StartLimit { get; init; } = 5;
    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);
    public double JitterFraction { get; init; } = 0.5;
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan CancelBudget { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan FreeBudget { get; init; } = TimeSpan.FromSeconds(30);
    public StartWindows Windows { get; init; } = new();
}

public sealed record StartAttemptInfo(StartReason Reason, int Attempt, int Limit, TimeSpan Delay);

public sealed class HostRestartReporter
{
    public Action<StartAttemptInfo> Attempting { get; init; } = static _ => { };
    public Action<StartAttemptInfo, string> AttemptFailed { get; init; } = static (_, _) => { };
    public Action<StartReason, int> Recovered { get; init; } = static (_, _) => { };
    public Action<int, TimeSpan, string> GaveUp { get; init; } = static (_, _, _) => { };
    public Action Stable { get; init; } = static () => { };
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

public sealed class HostSupervisor(
    IStartableHost host,
    IStartProgressSource progress,
    HostRestartReporter reporter,
    ISupervisorLog log,
    TimeProvider time,
    HostSupervisorSettings? settings = null,
    IProcessRestarter? restarter = null,
    Func<string>? diagnostics = null,
    Func<double>? random = null)
{
    public HostSupervisorSettings Settings { get; } = settings ?? new();

    public int FailuresInWindow => 0;

    public void Cancel()
    {
    }

    public async Task<StartResult> StartAsync(StartReason reason, bool retry = true)
    {
        try
        {
            await host.StartAsync(CancellationToken.None);
            return new StartResult(StartOutcome.Started, 1, null, null);
        }
        catch (Exception ex)
        {
            return new StartResult(StartOutcome.Failed, 1, ex.Message, ex);
        }
    }
}
