using System.Collections.Concurrent;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Services;

/// <summary>
/// Keeps every processor's stored health true (#577, #1227, #1261). The tray, System and the Dashboard read that
/// stored health and follow its push, so how soon it changes is how soon they do.
///
/// <para>It used to be one step of the maintenance lane, which waits 60 s after each whole run ends, so a
/// processor that came back was shown healthy 60 s plus a full maintenance run later, and never when automatic
/// jobs were off. This is its own loop, so none of that applies:</para>
/// <list type="bullet">
/// <item><b>Its own fixed schedule.</b> Each processor has a next-due time that moves on by its cadence from when
/// the last check was due, not from when it ended, so a slow check does not stretch the gap.</item>
/// <item><b>15 s while a processor is not healthy</b> (unreachable, degraded or refusing): the return is seen
/// within 15 s plus a 2 s tick, against a target of a minute. A check is one small GET with a 10 s timeout, so
/// four a minute per down processor is nothing next to what a hand-off or a Test already does.</item>
/// <item><b>30 s while healthy:</b> an outage shows within 30 s plus a 2 s tick, plus at most the 12 s a hanging
/// processor is given, which is still under the minute.</item>
/// <item><b>In parallel.</b> Each check is its own task with its own timeout; a processor that hangs is checked
/// once at a time and never holds up another.</item>
/// <item><b>Not tied to automatic jobs.</b> That switch governs work Deluno does, not whether it knows a
/// connection is up. Nothing here reads it.</item>
/// </list>
///
/// <para>Whoever else learns a processor's health (a hand-off, a Test) writes the same stored value; a processor
/// stored as not healthy by anyone is looked at again within 15 s of the last look. Every change is pushed by the
/// repository's <c>RecordProcessorConnectionHealthAsync</c>.</para>
/// </summary>
public sealed class ProcessorHealthWatcher(
    ILogger<ProcessorHealthWatcher> logger,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : BackgroundService
{
    /// <summary>How often a processor that is not healthy is looked at.</summary>
    public static readonly TimeSpan DownCadence = TimeSpan.FromSeconds(15);

    /// <summary>How often a healthy processor is looked at, so an outage shows inside a minute.</summary>
    public static readonly TimeSpan HealthyCadence = TimeSpan.FromSeconds(30);

    /// <summary>How often the loop wakes to see which processors are due.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    /// <summary>A little over the HTTP client's own 10 s, so a hang that the client misses is still cut off.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(12);

    private sealed class Schedule
    {
        public DateTimeOffset Due;
        public DateTimeOffset LastStarted;
        public Task? InFlight;
    }

    private readonly ConcurrentDictionary<string, Schedule> _schedules = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastRecordedUtc = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval, timeProvider);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "The processor health watcher could not finish a look; it will try again.");
            }
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// One look at which processors are due. Starts a check for each and returns at once with the tasks it started,
    /// so a check that hangs never holds up the loop or another processor.
    /// </summary>
    internal async Task<Task[]> TickAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ProcessorConnectionItem> connections;
        using (var scope = scopeFactory.CreateScope())
        {
            connections = (await scope.ServiceProvider.GetRequiredService<IProcessorRepository>()
                .ListProcessorConnectionsAsync(cancellationToken)).Where(item => item.IsEnabled).ToArray();
        }

        var keep = connections.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _schedules.Keys.Where(id => !keep.Contains(id)).ToArray())
        {
            _schedules.TryRemove(gone, out _);
        }

        var now = timeProvider.GetUtcNow();
        var started = new List<Task>();
        foreach (var connection in connections)
        {
            var schedule = _schedules.GetOrAdd(connection.Id, _ => new Schedule { Due = now, LastStarted = DateTimeOffset.MinValue });
            if (schedule.InFlight is { IsCompleted: false })
            {
                continue;
            }

            var healthy = IsHealthy(connection);
            var due = schedule.Due;
            if (!healthy && schedule.LastStarted != DateTimeOffset.MinValue)
            {
                // Someone else (a hand-off, a Test) stored it as not healthy: look again within the down cadence.
                var soonest = schedule.LastStarted + DownCadence;
                if (soonest < due)
                {
                    due = soonest;
                }
            }

            if (now < due)
            {
                continue;
            }

            // Fixed schedule: the next look is a cadence on from when this one was due. Only a long stall (the
            // machine slept) restarts the clock from now, so it never fires a burst to catch up.
            var cadence = healthy ? HealthyCadence : DownCadence;
            var basis = now - due < cadence ? due : now;
            schedule.LastStarted = now;
            schedule.Due = basis + cadence;
            var probe = ProbeAsync(connection, schedule, basis, cancellationToken);
            schedule.InFlight = probe;
            started.Add(probe);
        }

        return started.ToArray();
    }

    private static bool IsHealthy(ProcessorConnectionItem connection)
        => string.Equals(connection.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase);

    private async Task ProbeAsync(
        ProcessorConnectionItem connection,
        Schedule schedule,
        DateTimeOffset basis,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var services = scope.ServiceProvider;
            var processors = services.GetRequiredService<IProcessorRepository>();
            var connectionsService = services.GetRequiredService<IProcessorConnectionService>();

            ProcessorStatusSupport support;
            using var timeout = new CancellationTokenSource(ProbeTimeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                support = await connectionsService.GetStatusSupportAsync(connection, linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                support = new ProcessorStatusSupport(ProcessorAnswer.Unreachable, false, false, $"{connection.Name} did not answer in time.");
            }

            var healthy = support.Answer is not (ProcessorAnswer.Unreachable or ProcessorAnswer.Forbidden);
            await processors.RecordProcessorConnectionHealthAsync(
                connection.Id,
                healthy ? "healthy" : support.Answer == ProcessorAnswer.Forbidden ? "degraded" : "unreachable",
                healthy ? null : support.Message ?? $"{connection.Name} did not answer.",
                cancellationToken);

            // It answered: the healthy cadence applies from here, still measured from the schedule, not from now.
            schedule.Due = basis + (healthy ? HealthyCadence : DownCadence);
            await RecordPassAsync(services, "completed", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not check processor {ProcessorId}; it will be checked again.", connection.Id);
        }
    }

    /// <summary>Keeps "Check the processor" on System honest. At most once per healthy cadence, a single small write.</summary>
    private async Task RecordPassAsync(IServiceProvider services, string result, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now - _lastRecordedUtc < HealthyCadence || services.GetService<IJobQueueRepository>() is not { } jobs)
        {
            return;
        }

        _lastRecordedUtc = now;
        try
        {
            await jobs.RecordScheduledPassOutcomeAsync(
                SystemTasks.ProcessorHealth, now, result, 0, now + HealthyCadence, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Could not record the processor health pass.");
        }
    }
}
