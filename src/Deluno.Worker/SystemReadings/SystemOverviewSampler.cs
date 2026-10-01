using Deluno.Contracts;
using Deluno.Jobs.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.SystemReadings;

/// <summary>
/// Keeps the System screen alive (#1098). While a screen that shows it is open it takes a reading every
/// two seconds and pushes it as <c>SystemOverviewChanged</c> on the dashboard subject. While nobody is
/// watching it does almost nothing: one cheap reading every ten seconds, only so the ten-minute charts
/// are already full the moment someone opens the screen.
/// </summary>
public sealed class SystemOverviewSampler(
    SystemOverviewService overview,
    IRealtimeEventPublisher publisher,
    IRealtimeWatchers? watchers,
    TimeProvider timeProvider,
    ILogger<SystemOverviewSampler> logger)
    : BackgroundService
{
    public static readonly TimeSpan WatchedInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan UnwatchedInterval = TimeSpan.FromSeconds(10);

    /// <summary>The realtime subjects a screen joins while it can show the overview: the Dashboard, and Live (the Dashboard's other tab).</summary>
    private static readonly string[] Subjects = ["dashboard", LiveRealtimeSubject.Name];

    /// <summary>True when at least one screen that shows the overview is open.</summary>
    public static bool IsWatched(IRealtimeWatchers? watchers) => watchers is not null && Subjects.Any(subject => watchers.Count(subject) > 0);

    /// <summary>One turn of the loop: a pushed reading while watched, a history-only reading otherwise. Returns how long to wait before the next.</summary>
    public async Task<TimeSpan> TickAsync(CancellationToken cancellationToken)
    {
        if (!IsWatched(watchers))
        {
            await overview.SampleHistoryOnlyAsync(cancellationToken);
            return UnwatchedInterval;
        }

        var current = await overview.SampleAsync(cancellationToken);
        await publisher.PublishDashboardEventAsync(SystemOverviewEventNames.Changed, current, cancellationToken);
        return WatchedInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wait = UnwatchedInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(wait, timeProvider, stoppingToken);
                wait = await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A reading that fails is a gap in a chart, never a reason to stop the worker.
                logger.LogDebug(exception, "A System overview reading was skipped.");
                wait = WatchedInterval;
            }
        }
    }
}

/// <summary>
/// The records the System screen needs that are only true if they are kept all the time, whether or not
/// anyone is watching: each start of Deluno, and each drive's free space once a day.
/// </summary>
public sealed class SystemOverviewRecorder(
    IServiceScopeFactory scopeFactory,
    ISystemOverviewFacts facts,
    TimeProvider timeProvider,
    ILogger<SystemOverviewRecorder> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using (var scope = scopeFactory.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<ISystemOverviewStore>()
                    .RecordStartAsync(timeProvider.GetUtcNow(), stoppingToken);
            }

            // Give the Dashboard's disk reading its first pass before asking for it.
            await Task.Delay(TimeSpan.FromSeconds(30), timeProvider, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RecordDriveSpaceAsync(stoppingToken);
                await Task.Delay(Interval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Deluno could not keep its System records; the screen will have no drive-full forecast or restart count.");
        }
    }

    /// <summary>Stores today's free space for every drive; the last reading of the day stays.</summary>
    public async Task RecordDriveSpaceAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetLocalNow().Date);
        var drives = await facts.ReadDrivesAsync(cancellationToken);
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISystemOverviewStore>();
        foreach (var drive in drives)
        {
            await store.RecordDriveSpaceAsync(drive.VolumeKey, today, drive.FreeBytes, drive.TotalBytes, cancellationToken);
        }

        await store.PruneAsync(timeProvider.GetUtcNow() - Retention, cancellationToken);
    }
}
