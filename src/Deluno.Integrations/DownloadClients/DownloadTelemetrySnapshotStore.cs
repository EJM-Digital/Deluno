using Deluno.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Integrations.DownloadClients;

/// <summary>One computed value and the moment it was captured.</summary>
public sealed record SnapshotEntry<T>(T Value, DateTimeOffset CapturedUtc) where T : class;

/// <summary>
/// A last-known answer that is recomputed by one caller at a time (#1081).
/// <see cref="RefreshAsync"/> awaits a recompute, and concurrent callers share
/// the one in flight. <see cref="LatestAsync"/> returns what is held at once
/// and, when it is older than the caller's limit, starts a recompute in the
/// background without waiting for it. A recompute that fails keeps the last
/// good answer.
/// </summary>
public sealed class SingleFlightSnapshot<T>(
    Func<CancellationToken, Task<T>> compute,
    TimeProvider timeProvider,
    ILogger logger,
    string name)
    where T : class
{
    private static readonly TimeSpan ComputeLimit = TimeSpan.FromSeconds(90);

    private readonly object gate = new();
    private SnapshotEntry<T>? current;
    private Task<SnapshotEntry<T>>? inFlight;
    private bool stale;

    public SnapshotEntry<T>? Current
    {
        get { lock (gate) { return current; } }
    }

    /// <summary>Replaces the held answer with an edit of it, keeping its capture time.</summary>
    public void Update(Func<T, T> edit)
    {
        lock (gate)
        {
            if (current is not null)
            {
                current = current with { Value = edit(current.Value) };
            }
        }
    }

    /// <summary>Marks the held answer as out of date, so the next reader refreshes it.</summary>
    public void MarkStale()
    {
        lock (gate) { stale = true; }
    }

    /// <summary>
    /// Returns the held answer at once. When there is none, or it is older than
    /// <paramref name="maxAge"/> (or was marked stale), a refresh starts in the
    /// background. With nothing held yet, waits up to <paramref name="coldWait"/>
    /// for the first one, so the very first screen after a start is not blank.
    /// </summary>
    public async Task<SnapshotEntry<T>?> LatestAsync(TimeSpan maxAge, TimeSpan coldWait, CancellationToken cancellationToken)
    {
        Task<SnapshotEntry<T>>? started = null;
        SnapshotEntry<T>? held;
        lock (gate)
        {
            held = current;
            if (held is null || stale || timeProvider.GetUtcNow() - held.CapturedUtc > maxAge)
            {
                started = StartLocked();
            }
        }

        if (held is not null || started is null || coldWait <= TimeSpan.Zero)
        {
            if (started is not null)
            {
                _ = ObserveAsync(started);
            }

            return held;
        }

        try
        {
            return await started.WaitAsync(coldWait, cancellationToken);
        }
        catch (TimeoutException)
        {
            _ = ObserveAsync(started);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "First {Snapshot} capture failed.", name);
            return null;
        }
    }

    /// <summary>
    /// The held answer when it is younger than <paramref name="reuseWithin"/>,
    /// otherwise a recompute shared with every other caller. Waits for it.
    /// </summary>
    public async Task<SnapshotEntry<T>> RefreshAsync(TimeSpan reuseWithin, CancellationToken cancellationToken)
    {
        Task<SnapshotEntry<T>> task;
        lock (gate)
        {
            if (current is not null && !stale && timeProvider.GetUtcNow() - current.CapturedUtc <= reuseWithin)
            {
                return current;
            }

            task = StartLocked();
        }

        return await task.WaitAsync(cancellationToken);
    }

    private Task<SnapshotEntry<T>> StartLocked()
    {
        if (inFlight is { IsCompleted: false })
        {
            return inFlight;
        }

        // Not tied to any one caller's token: a browser that navigates away must
        // not cancel the refresh everyone else is waiting on.
        inFlight = Task.Run(async () =>
        {
            using var limit = new CancellationTokenSource(ComputeLimit);
            var value = await compute(limit.Token);
            var entry = new SnapshotEntry<T>(value, timeProvider.GetUtcNow());
            lock (gate)
            {
                current = entry;
                stale = false;
            }

            return entry;
        });
        return inFlight;
    }

    private async Task ObserveAsync(Task<SnapshotEntry<T>> task)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Background {Snapshot} refresh failed; the last answer stays.", name);
        }
    }
}

/// <summary>
/// The last thing the download clients said, captured by one background
/// refresher and read by everything else (#1081). Screens, the sidebar and the
/// gates read it and never wait on a client; the worker's planner asks for a
/// fresh one explicitly. Every reader shares one in-flight refresh.
/// </summary>
public interface IDownloadTelemetrySnapshotStore
{
    DownloadTelemetryCapture? Current { get; }

    DateTimeOffset? CapturedUtc { get; }

    /// <summary>Returns the latest capture at once; an old one starts a background refresh. Null only before the very first capture.</summary>
    Task<SnapshotEntry<DownloadTelemetryCapture>?> GetLatestAsync(TimeSpan maxAge, CancellationToken cancellationToken);

    /// <summary>The latest capture when younger than <paramref name="reuseWithin"/>, otherwise one shared refresh, awaited.</summary>
    Task<SnapshotEntry<DownloadTelemetryCapture>> RefreshAsync(TimeSpan reuseWithin, CancellationToken cancellationToken);

    /// <summary>Says the held capture is out of date (after a cleanup, say), so the next reader refreshes.</summary>
    void MarkStale();
}

public sealed class DownloadTelemetrySnapshotStore : IDownloadTelemetrySnapshotStore
{
    /// <summary>
    /// How long the very first request after a start may wait for the first
    /// capture. Only ever applies before any capture exists.
    /// </summary>
    internal static readonly TimeSpan DefaultColdWait = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// <see cref="DefaultColdWait"/> in the product. A test that is about readers sharing one pass, not about this
    /// budget, sets it long, so a slow machine cannot turn a shared pass into a cold-wait timeout.
    /// </summary>
    internal TimeSpan ColdWait { get; set; } = DefaultColdWait;

    private readonly SingleFlightSnapshot<DownloadTelemetryCapture> cell;

    public DownloadTelemetrySnapshotStore(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<DownloadTelemetrySnapshotStore> logger)
    {
        cell = new SingleFlightSnapshot<DownloadTelemetryCapture>(
            async cancellationToken =>
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IDownloadClientTelemetryService>();
                return await service.CaptureAsync(cancellationToken);
            },
            timeProvider,
            logger,
            "download telemetry");
    }

    public DownloadTelemetryCapture? Current => cell.Current?.Value;

    public DateTimeOffset? CapturedUtc => cell.Current?.CapturedUtc;

    public Task<SnapshotEntry<DownloadTelemetryCapture>?> GetLatestAsync(TimeSpan maxAge, CancellationToken cancellationToken)
        => cell.LatestAsync(maxAge, ColdWait, cancellationToken);

    public Task<SnapshotEntry<DownloadTelemetryCapture>> RefreshAsync(TimeSpan reuseWithin, CancellationToken cancellationToken)
        => cell.RefreshAsync(reuseWithin, cancellationToken);

    public void MarkStale() => cell.MarkStale();
}

/// <summary>
/// The one background refresher of the download-client snapshot (#1081), on the
/// cadence the progress publisher always had: every couple of seconds while
/// anything is moving, every twenty when nothing is. Everything that used to
/// call the clients on its own now reads what this captured.
/// </summary>
public sealed class DownloadTelemetryRefreshService(
    IDownloadTelemetrySnapshotStore store,
    TimeProvider timeProvider,
    ILogger<DownloadTelemetryRefreshService> logger,
    IRealtimeWatchers? watchers = null)
    : BackgroundService
{
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan WatchedActiveInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = StartupDelay;
        var reportedFailure = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                // Zero reuse: the whole point of this loop is a new reading.
                var entry = await store.RefreshAsync(TimeSpan.FromTicks(-1), stoppingToken);
                interval = NextInterval(AnythingMoving(entry.Value.Overview), watchers is not null && watchers.Count(LiveRealtimeSubject.Name) > 0);
                reportedFailure = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                interval = IdleInterval;
                if (reportedFailure)
                {
                    logger.LogDebug(exception, "Download client refresh skipped.");
                }
                else
                {
                    reportedFailure = true;
                    logger.LogWarning(exception, "Refreshing what the download clients are doing failed; the last reading stays until the next try.");
                }
            }
        }
    }

    /// <summary>Once a second while something is moving and a screen watches (#1097); otherwise the ordinary cadence.</summary>
    internal static TimeSpan NextInterval(bool moving, bool watched)
        => !moving ? IdleInterval : watched ? WatchedActiveInterval : ActiveInterval;

    internal static bool AnythingMoving(DownloadTelemetryOverview overview)
        => overview.Clients
            .SelectMany(client => client.Queue)
            .Any(item => item.Status is DownloadQueueStatuses.Downloading or DownloadQueueStatuses.Stalled or DownloadQueueStatuses.Queued or DownloadQueueStatuses.NotTransferring);
}

/// <summary>How old a capture each kind of reader accepts before asking for another (#1081).</summary>
public static class DownloadTelemetryFreshness
{
    /// <summary>Screens and the sidebar: served at once; anything older starts a background refresh.</summary>
    public static readonly TimeSpan Screen = TimeSpan.FromSeconds(5);

    /// <summary>Background loops that share the refresher's own cadence: reuse a reading taken within the last second.</summary>
    public static readonly TimeSpan Loop = TimeSpan.FromSeconds(1);

    /// <summary>The planner and gates that act on what they read.</summary>
    public static readonly TimeSpan Acting = TimeSpan.FromSeconds(2);

    /// <summary>Slow-moving checks (free disk space).</summary>
    public static readonly TimeSpan Relaxed = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The orphaned-downloads list, scanned in the background and served from memory
/// (#1081). The folder walk and the size of every orphan used to run inside the
/// Transfers request and the sidebar's 45-second poll; now a request reads this
/// and a refresher re-scans about once a minute. A Keep or Remove edits the held
/// list at once, so the screen is right the moment the action returns.
/// </summary>
public sealed class OrphanedDownloadCache
{
    private readonly SingleFlightSnapshot<IReadOnlyList<OrphanedDownloadItem>> cell;

    public OrphanedDownloadCache(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<OrphanedDownloadCache> logger)
    {
        cell = new SingleFlightSnapshot<IReadOnlyList<OrphanedDownloadItem>>(
            async cancellationToken =>
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IOrphanedDownloadService>();
                return await service.ScanAsync(cancellationToken);
            },
            timeProvider,
            logger,
            "orphaned downloads");
    }

    public SnapshotEntry<IReadOnlyList<OrphanedDownloadItem>>? Current => cell.Current;

    /// <summary>The held list at once; one older than <paramref name="maxAge"/> re-scans in the background.</summary>
    public async Task<IReadOnlyList<OrphanedDownloadItem>> GetLatestAsync(TimeSpan maxAge, CancellationToken cancellationToken)
        => (await cell.LatestAsync(maxAge, DownloadTelemetrySnapshotStore.DefaultColdWait, cancellationToken))?.Value ?? [];

    public Task<SnapshotEntry<IReadOnlyList<OrphanedDownloadItem>>> RefreshAsync(CancellationToken cancellationToken)
        => cell.RefreshAsync(TimeSpan.FromTicks(-1), cancellationToken);

    /// <summary>Drops one path from the held list, after a person kept or removed it.</summary>
    public void Forget(string path)
    {
        var key = OrphanedDownloads.Key(path ?? string.Empty);
        cell.Update(items => items
            .Where(item => !string.Equals(OrphanedDownloads.Key(item.Path), key, StringComparison.OrdinalIgnoreCase))
            .ToArray());
    }
}

/// <summary>Re-scans the download folders for orphans about once a minute (#1081).</summary>
public sealed class OrphanedDownloadRefreshService(
    OrphanedDownloadCache cache,
    TimeProvider timeProvider,
    ILogger<OrphanedDownloadRefreshService> logger)
    : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = StartupDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken);
                await cache.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Orphaned download scan skipped; the last list stays.");
            }

            delay = Interval;
        }
    }
}
