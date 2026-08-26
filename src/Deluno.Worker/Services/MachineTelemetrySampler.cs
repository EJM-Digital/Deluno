using Deluno.Infrastructure.Observability;
using Deluno.Jobs.Data;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Services;

/// <summary>
/// Records how hard the machine is working, on a fixed cadence (#272).
///
/// Modelled directly on <see cref="DownloadThroughputSampler"/>, including its
/// view that a missing sample is a gap in a chart rather than a failure worth
/// taking the worker down for — a machine under enough load to refuse a reading
/// is exactly the situation someone opens this graph to understand.
///
/// It is also the *only* thing that probes. Rates need two points, so a second
/// caller taking its own reading would reset this one's baseline and both would
/// report nonsense. The dashboard reads the newest stored row instead, which
/// costs it up to a minute of staleness on numbers that are already a
/// per-minute series.
/// </summary>
public sealed class MachineTelemetrySampler(
    ILogger<MachineTelemetrySampler> logger,
    IServiceScopeFactory scopeFactory,
    IMachineProbe probe,
    TimeProvider timeProvider)
    : BackgroundService
{
    /// <summary>
    /// One reading a minute — the same cadence as download throughput, so the
    /// two series line up on a shared x-axis without resampling.
    /// </summary>
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(1);

    /// <summary>Long enough to cover "what happened last night", short enough that the table stays trivial.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromHours(48);

    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    private DateTimeOffset _lastPruneUtc = DateTimeOffset.MinValue;
    private bool _reportedFailure;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SampleInterval, timeProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SampleAsync(stoppingToken);
                _reportedFailure = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // The first failure is said out loud. A permanently broken
                // sampler — a missing registration, say — looks identical to a
                // transient one at debug level, and would otherwise leave the
                // chart quietly empty forever.
                if (_reportedFailure)
                {
                    logger.LogDebug(exception, "Machine telemetry sample skipped.");
                }
                else
                {
                    _reportedFailure = true;
                    logger.LogWarning(exception, "Machine telemetry sampling failed; the machine chart will have gaps.");
                }
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SampleAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMachineTelemetryRepository>();

        // Measured against the library drive rather than wherever Deluno's own
        // database happens to live: "is the drive saturated" is a question about
        // the volume the media is moving on to.
        var sample = probe.Read(await ResolveLibraryVolumeAsync(scope.ServiceProvider, cancellationToken));
        await repository.RecordSampleAsync(sample, cancellationToken);

        var now = timeProvider.GetUtcNow();
        if (now - _lastPruneUtc < PruneInterval)
        {
            return;
        }

        _lastPruneUtc = now;
        var removed = await repository.PruneAsync(now - Retention, cancellationToken);
        if (removed > 0)
        {
            logger.LogDebug("Pruned {Count} machine telemetry samples past retention.", removed);
        }
    }

    /// <summary>
    /// The first configured library root, falling back to the movie root in
    /// settings. Null when nothing is configured yet, which simply means the
    /// whole-volume series stays empty until it is.
    /// </summary>
    private static async Task<string?> ResolveLibraryVolumeAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        try
        {
            var libraries = await services
                .GetRequiredService<Deluno.Libraries.Data.ILibrariesRepository>()
                .ListLibrariesAsync(cancellationToken);
            var root = libraries
                .Select(library => library.RootPath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            if (!string.IsNullOrWhiteSpace(root))
            {
                return root;
            }

            var settings = await services.GetRequiredService<IPlatformSettingsRepository>().GetAsync(cancellationToken);
            return settings.MovieRootPath ?? settings.SeriesRootPath;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }
}
