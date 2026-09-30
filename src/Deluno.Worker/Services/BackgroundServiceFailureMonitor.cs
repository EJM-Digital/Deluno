using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Deluno.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Services;

/// <summary>One background service that ended with an error and is no longer running.</summary>
public sealed record FailedBackgroundService(
    string TypeName,
    string DisplayName,
    string Reason,
    DateTimeOffset FailedUtc);

/// <summary>A background job that is still running but keeps failing, or a start-up tidy-up that did not finish (#1080).</summary>
public sealed record BackgroundProblem(
    string Key,
    string Title,
    string Message,
    string Reason,
    DateTimeOffset SinceUtc);

/// <summary>What Health reads to say a background service has stopped (#1080).</summary>
public interface IBackgroundServiceFailureState
{
    IReadOnlyList<FailedBackgroundService> Failures { get; }

    /// <summary>Jobs that are failing repeatedly without having stopped.</summary>
    IReadOnlyList<BackgroundProblem> Problems { get; }
}

/// <summary>
/// Which services are critical and how hard Deluno tries to keep them alive (#1080).
///
/// <para><b>Critical services</b> are the two whose death would leave Deluno
/// looking healthy while doing nothing, or unable to say so: the job worker
/// (<c>DelunoHeartbeatWorker</c>, which runs every lane) and the health check
/// service (<c>HealthCheckHostedService</c>, the only thing that can put a
/// problem on Health). If one ends, it is restarted in this process with a
/// growing pause, up to <see cref="MaxRestarts"/> times in a row. If it cannot
/// be kept running the host is stopped with a failing exit code, so Docker or
/// the Windows service restarts Deluno. Every other background service is not
/// critical: its failure is logged and shown on Health, and the rest carries on.</para>
/// </summary>
public sealed class BackgroundServiceSafetyOptions
{
    /// <summary>The type names of the critical services.</summary>
    public ISet<string> CriticalServiceTypeNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "DelunoHeartbeatWorker",
        "HealthCheckHostedService"
    };

    /// <summary>Restarts in a row before Deluno gives up and stops the host.</summary>
    public int MaxRestarts { get; set; } = 5;

    /// <summary>The pause before the first restart; it doubles each time.</summary>
    public TimeSpan RestartBackoff { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>A service that then runs this long is trusted again, and its restart count starts over.</summary>
    public TimeSpan StableAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Sets a failing process exit code when giving up, so an orchestrator restarts Deluno.</summary>
    public bool SetExitCodeWhenGivingUp { get; set; } = true;

    /// <summary>
    /// When set, giving up ends the process through this instead of a graceful
    /// host stop. The installed Windows service needs it: a host that stops
    /// itself is reported to the service manager as a clean stop, so its
    /// restart-on-failure actions never run. A process that ends while the
    /// service is still Running is treated as a crash, and they do.
    /// </summary>
    public Action<int>? ExitProcess { get; set; }

    /// <summary>
    /// Called with <see cref="ServiceFailureExitCode"/> just before the host is stopped
    /// on a give-up, so the Windows service lifetime reports that non-zero code to the
    /// service manager when it stops (with <c>failureflag 1</c> that runs the restart
    /// actions after a graceful stop). Never called for a normal stop.
    /// </summary>
    public Action<int>? SetServiceExitCode { get; set; }

    /// <summary>Win32 ERROR_PROCESS_ABORTED: what the service manager is told when Deluno stops itself because a critical service would not stay up.</summary>
    public const int ServiceFailureExitCode = 1067;

    /// <summary>After the host has stopped, how long the process is given to end (and tell the service manager) on its own before <see cref="ExitProcess"/> is called.</summary>
    public TimeSpan ExitGrace { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How long to wait after the last log line before <see cref="ExitProcess"/> is called, so the log file catches up.</summary>
    public TimeSpan ExitDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long to let the host finish stopping (hosted services' StopAsync) before <see cref="ExitProcess"/> is called anyway.</summary>
    public TimeSpan StopWait { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// The safety net under every <see cref="BackgroundService"/> (#1080).
///
/// <para>In .NET the default is that one background service throwing stops the
/// whole host. Deluno sets
/// <see cref="BackgroundServiceExceptionBehavior.Ignore"/> instead, so the
/// rest of the app keeps running, and this monitor watches each service's
/// <see cref="BackgroundService.ExecuteTask"/>: if one ends with an error it is
/// logged and shown on Health in plain words until Deluno is restarted.</para>
///
/// <para>Every loop still catches its own per-pass failures and carries on;
/// this only catches what one of them missed. A service finishing normally
/// (a feature that is switched off returns straight away) is not a failure.
/// A failure in a hosted service's <c>StartAsync</c> (the database
/// migrations) still stops the host, deliberately: Deluno must not run on a
/// database it could not prepare.</para>
/// </summary>
public sealed class BackgroundServiceFailureMonitor(
    IServiceProvider serviceProvider,
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime lifetime,
    TimeProvider timeProvider,
    ILogger<BackgroundServiceFailureMonitor> logger,
    BackgroundServiceSafetyOptions? options = null)
    : IHostedLifecycleService, IBackgroundServiceFailureState, IBackgroundProblemReporter
{
    private readonly BackgroundServiceSafetyOptions _options = options ?? new BackgroundServiceSafetyOptions();
    private readonly ConcurrentDictionary<string, FailedBackgroundService> _failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BackgroundProblem> _problems = new(StringComparer.Ordinal);

    public IReadOnlyList<BackgroundProblem> Problems
        => [.. _problems.Values.OrderBy(problem => problem.SinceUtc)];

    public void Report(string key, string title, string message, string reason)
    {
        var isNew = !_problems.TryGetValue(key, out var existing);
        _problems[key] = new BackgroundProblem(key, title, message, reason, existing?.SinceUtc ?? timeProvider.GetUtcNow());
        if (isNew)
        {
            _ = RequestHealthRunAsync(key);
        }
    }

    public void Clear(string key)
    {
        if (_problems.TryRemove(key, out _))
        {
            _ = RequestHealthRunAsync(key);
        }
    }

    public IReadOnlyList<FailedBackgroundService> Failures
        => [.. _failures.Values.OrderBy(failure => failure.FailedUtc)];

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Runs once every hosted service has started, so each one's task exists.</summary>
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        foreach (var service in serviceProvider.GetServices<IHostedService>())
        {
            if (service is BackgroundService background && !ReferenceEquals(background, this))
            {
                _ = WatchAsync(background);
            }
        }

        return Task.CompletedTask;
    }

    internal async Task WatchAsync(BackgroundService service)
    {
        var type = service.GetType();
        var critical = _options.CriticalServiceTypeNames.Contains(type.Name);
        var restarts = 0;

        while (true)
        {
            var task = service.ExecuteTask;
            if (task is null)
            {
                return;
            }

            var startedUtc = timeProvider.GetUtcNow();
            Exception? failure = null;
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            if (lifetime.ApplicationStopping.IsCancellationRequested)
            {
                return;
            }

            if (!critical)
            {
                if (failure is not null)
                {
                    await RecordAsync(type, failure).ConfigureAwait(false);
                }

                return;
            }

            // A critical service must not end while Deluno is running, even
            // quietly. Restart it here; if it will not stay up, stop the host
            // so Docker or the Windows service starts Deluno again.
            failure ??= new InvalidOperationException($"{type.Name} ended without being asked to stop.");
            if (timeProvider.GetUtcNow() - startedUtc >= _options.StableAfter)
            {
                restarts = 0;
            }

            if (restarts >= _options.MaxRestarts)
            {
                await RecordAsync(type, failure).ConfigureAwait(false);
                logger.LogCritical(failure, "Critical background service {Service} could not be kept running after {Restarts} restarts; stopping Deluno so it is started again.", type.Name, restarts);
                if (_options.ExitProcess is { } exitProcess)
                {
                    try
                    {
                        await Task.Delay(_options.ExitDelay).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // The pause only lets the log catch up.
                    }

                    // A person's stop (or an update's) that arrived meanwhile is a normal stop:
                    // never turn it into a crash the service manager would restart.
                    if (lifetime.ApplicationStopping.IsCancellationRequested)
                    {
                        return;
                    }

                    // Stop the host properly first so hosted services run their StopAsync and
                    // in-flight work ends, then end the process with the failing exit code.
                    var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var registration = lifetime.ApplicationStopped.Register(() => stopped.TrySetResult());
                    try
                    {
                        // The service lifetime reports this code when the host stops, so the
                        // service manager sees a failure and runs the restart actions.
                        _options.SetServiceExitCode?.Invoke(BackgroundServiceSafetyOptions.ServiceFailureExitCode);
                    }
                    catch (Exception exitCodeFailure)
                    {
                        logger.LogWarning(exitCodeFailure, "Deluno could not set the service's failing exit code; the process is ended below instead.");
                    }

                    lifetime.StopApplication();
                    try
                    {
                        await stopped.Task.WaitAsync(_options.StopWait).ConfigureAwait(false);
                        // Let the process end and report the stop by itself; end it only if it has not.
                        await Task.Delay(_options.ExitGrace).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        logger.LogWarning("Deluno did not finish stopping within {Wait}; ending the process now.", _options.StopWait);
                    }

                    exitProcess(1);
                    return;
                }

                if (_options.SetExitCodeWhenGivingUp)
                {
                    Environment.ExitCode = 1;
                }

                lifetime.StopApplication();
                return;
            }

            restarts++;
            var pause = TimeSpan.FromTicks(_options.RestartBackoff.Ticks * (1L << Math.Min(restarts - 1, 10)));
            logger.LogError(failure, "Critical background service {Service} stopped after an error; restarting it in {Pause} (attempt {Attempt} of {Max}).", type.Name, pause, restarts, _options.MaxRestarts);
            try
            {
                await Task.Delay(pause, timeProvider, lifetime.ApplicationStopping).ConfigureAwait(false);
                await service.StartAsync(lifetime.ApplicationStopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception startFailure)
            {
                // It threw before its first await. ExecuteTask still points at
                // the previous run, so the next pass would see the same failure;
                // that is fine, it is counted against the restart limit.
                logger.LogError(startFailure, "Restarting {Service} failed.", type.Name);
            }
        }
    }

    private async Task RequestHealthRunAsync(string reason)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var health = scope.ServiceProvider.GetService<IHealthCheckRepository>();
            if (health is not null)
            {
                await health.RequestImmediateRunAsync(HealthCheckCategories.System, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception requestFailure)
        {
            logger.LogDebug(requestFailure, "Could not ask for an immediate health check after {Reason} changed.", reason);
        }
    }

    private async Task RecordAsync(Type type, Exception exception)
    {
        var failure = new FailedBackgroundService(
            type.Name,
            FriendlyName(type.Name),
            exception.Message,
            timeProvider.GetUtcNow());
        _failures[type.Name] = failure;
        logger.LogError(exception, "Background service {Service} stopped after an error; Deluno keeps running and Health shows it.", type.Name);

        try
        {
            using var scope = scopeFactory.CreateScope();
            var health = scope.ServiceProvider.GetService<IHealthCheckRepository>();
            if (health is not null)
            {
                await health.RequestImmediateRunAsync(HealthCheckCategories.System, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception requestFailure)
        {
            logger.LogDebug(requestFailure, "Could not ask for an immediate health check after {Service} stopped.", type.Name);
        }
    }

    /// <summary>"RankingModelTrainingHostedService" becomes "Ranking model training".</summary>
    internal static string FriendlyName(string typeName)
    {
        var trimmed = typeName;
        foreach (var suffix in new[] { "HostedService", "Service" })
        {
            if (trimmed.Length > suffix.Length && trimmed.EndsWith(suffix, StringComparison.Ordinal))
            {
                trimmed = trimmed[..^suffix.Length];
                break;
            }
        }

        var words = Regex.Replace(trimmed, "(?<=[a-z0-9])(?=[A-Z])", " ").Trim();
        if (words.Length == 0)
        {
            return typeName;
        }

        var builder = new StringBuilder(words.ToLowerInvariant());
        builder[0] = char.ToUpperInvariant(builder[0]);
        return builder.ToString();
    }
}
