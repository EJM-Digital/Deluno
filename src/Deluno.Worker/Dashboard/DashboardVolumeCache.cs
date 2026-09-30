using Deluno.Infrastructure.Storage;
using Deluno.Libraries.Data;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Worker.Dashboard;

/// <summary>
/// The Dashboard's storage panel, read off the request.
///
/// <para>Sizing a disk is a system call, and sizing a network share can hang
/// for tens of seconds when the share is gone. Neither may happen while a
/// person waits for a screen (#1081), so a background service reads every
/// relevant folder about once a minute and this holds the answer. A request
/// only reads the last answer; before the first one exists it starts the read
/// in the background and reports no volumes yet.</para>
/// </summary>
public sealed class DashboardVolumeCache(
    IServiceScopeFactory scopeFactory,
    IOptions<StoragePathOptions> storageOptions,
    TimeProvider timeProvider,
    ILogger<DashboardVolumeCache> logger)
{
    /// <summary>How long one folder may take to size before it is reported unavailable.</summary>
    public static readonly TimeSpan PerFolderBudget = TimeSpan.FromSeconds(4);

    private readonly object _gate = new();
    private DashboardVolumeReading? _latest;
    private Task? _refresh;

    public DashboardVolumeReading? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    /// <summary>Starts a first reading in the background when there is none yet. Never waits.</summary>
    public void EnsureReading()
    {
        lock (_gate)
        {
            if (_latest is not null || _refresh is not null)
            {
                return;
            }
        }

        _ = RefreshAsync(CancellationToken.None);
    }

    /// <summary>Reads every folder now. Callers are the background refresher and tests, never a request.</summary>
    public Task RefreshAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _refresh ??= Task.Run(async () =>
            {
                try
                {
                    var reading = await ReadAsync(cancellationToken);
                    lock (_gate)
                    {
                        _latest = reading;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "The Dashboard's disk reading failed; the last one stays.");
                }
                finally
                {
                    lock (_gate)
                    {
                        _refresh = null;
                    }
                }
            }, CancellationToken.None);
            return _refresh;
        }
    }

    private async Task<DashboardVolumeReading> ReadAsync(CancellationToken cancellationToken)
    {
        var places = new List<(string Path, string Owner, string Role)>
        {
            (Path.GetFullPath(storageOptions.Value.DataRoot), "Deluno's own data", "data")
        };

        using (var scope = scopeFactory.CreateScope())
        {
            var libraries = await scope.ServiceProvider.GetRequiredService<ILibrariesRepository>().ListLibrariesAsync(cancellationToken);
            foreach (var library in libraries)
            {
                places.Add((library.RootPath, library.Name, "library"));
                if (!string.IsNullOrWhiteSpace(library.DownloadsPath))
                {
                    places.Add((library.DownloadsPath, $"{library.Name} downloads", "downloads"));
                }

                if (!string.IsNullOrWhiteSpace(library.ProcessorOutputPath))
                {
                    places.Add((library.ProcessorOutputPath, $"{library.Name} processor output", "processor"));
                }
            }

            var settings = await scope.ServiceProvider.GetRequiredService<IPlatformSettingsRepository>().GetAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(settings.IncompleteDownloadsPath))
            {
                places.Add((settings.IncompleteDownloadsPath, "Incomplete downloads", "downloads"));
            }
        }

        // Size every folder at once so one dead share costs one budget, not one each.
        var readings = await Task.WhenAll(places.Select(async place =>
        {
            var read = Task.Run(() => VolumeSpace.TryRead(place.Path), CancellationToken.None);
            try
            {
                return (place, Reading: await read.WaitAsync(PerFolderBudget, cancellationToken));
            }
            catch (TimeoutException)
            {
                return (place, Reading: (VolumeSpaceReading?)null);
            }
        }));

        var volumes = new List<DashboardVolume>();
        foreach (var group in readings.Where(item => item.Reading is not null).GroupBy(item => item.Reading!.VolumeKey))
        {
            var first = group.First().Reading!;
            volumes.Add(new DashboardVolume(
                Id: group.Key,
                Label: string.Join(" · ", group.Select(item => item.place.Owner).Distinct().Take(3)),
                Roles: group.Select(item => item.place.Role).Distinct().ToArray(),
                Paths: group.Select(item => item.place.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Available: true,
                TotalBytes: first.TotalBytes,
                FreeBytes: first.FreeBytes));
        }

        // What could not be read is still listed, by its folder, so an offline
        // share shows up as a problem rather than silently disappearing.
        foreach (var missing in readings.Where(item => item.Reading is null))
        {
            volumes.Add(new DashboardVolume(
                Id: $"unavailable:{missing.place.Path}",
                Label: missing.place.Owner,
                Roles: [missing.place.Role],
                Paths: [missing.place.Path],
                Available: false,
                TotalBytes: null,
                FreeBytes: null));
        }

        return new DashboardVolumeReading(timeProvider.GetUtcNow(), volumes);
    }
}

public sealed record DashboardVolumeReading(DateTimeOffset ReadingUtc, IReadOnlyList<DashboardVolume> Volumes);

/// <summary>Refreshes <see cref="DashboardVolumeCache"/> about once a minute, off every request.</summary>
public sealed class DashboardVolumeRefresher(DashboardVolumeCache cache, TimeProvider timeProvider) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await cache.RefreshAsync(stoppingToken);
                await Task.Delay(Interval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
