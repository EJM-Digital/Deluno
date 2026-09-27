using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Services;

/// <summary>
/// Records combined throughput, both directions, on a configurable cadence, so
/// the dashboard can show what the speed has been rather than only what it is.
///
/// Everything else on that dashboard counts stored rows; speed is a measurement
/// and nothing was measuring it. Without this the live wave could only ever
/// cover the seconds since a page was opened, which cannot answer "was it slow
/// overnight" — the question a speed graph exists for.
///
/// The sampler is deliberately dull: read the overview the telemetry service
/// already assembles, store one row, and periodically drop rows past the
/// retention window. It never queries a client directly and it never writes
/// anything but its own table.
/// </summary>
public sealed class DownloadThroughputSampler(
    ILogger<DownloadThroughputSampler> logger,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider)
    : BackgroundService
{
    /// <summary>Fallback cadence before settings can be read even once.</summary>
    private static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How much history is kept. Long enough to cover "what happened last
    /// night", short enough that the table stays trivial.
    /// </summary>
    private static readonly TimeSpan Retention = TimeSpan.FromHours(48);

    /// <summary>Pruning every sample would be wasteful; hourly is plenty.</summary>
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    private DateTimeOffset _lastPruneUtc = DateTimeOffset.MinValue;
    private bool _reportedFailure;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = DefaultSampleInterval;
            try
            {
                interval = await SampleAsync(stoppingToken);
                _reportedFailure = false;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A missing sample is a gap in a chart, not a failure worth
                // taking the worker down for: an unreachable client is exactly
                // the situation someone opens this graph to understand.
                //
                // The first failure is still said out loud. A permanently
                // broken sampler — a missing registration, say — looks
                // identical to a transient one at debug level, and would
                // otherwise leave the chart quietly empty forever.
                if (_reportedFailure)
                {
                    logger.LogDebug(exception, "Download throughput sample skipped.");
                }
                else
                {
                    _reportedFailure = true;
                    logger.LogWarning(exception, "Download throughput sampling failed; the speed chart will have gaps.");
                }
            }

            try
            {
                // #823: read fresh each tick rather than one PeriodicTimer
                // fixed at startup, so a changed interval takes effect on the
                // very next tick without a restart.
                await Task.Delay(interval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Samples once and returns the interval to wait before the next one.</summary>
    private async Task<TimeSpan> SampleAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var telemetry = scope.ServiceProvider.GetRequiredService<IDownloadClientTelemetryService>();
        var repository = scope.ServiceProvider.GetRequiredService<IDownloadThroughputRepository>();
        var platformSettingsRepository = scope.ServiceProvider.GetRequiredService<IPlatformSettingsRepository>();
        var settings = await platformSettingsRepository.GetAsync(cancellationToken);
        var interval = TimeSpan.FromMinutes(Math.Clamp(settings.DownloadThroughputSamplerIntervalMinutes, 1, 60));

        var overview = await telemetry.GetOverviewAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        await repository.RecordSampleAsync(
            new DownloadThroughputSample(
                CapturedUtc: now,
                SpeedMbps: overview.Summary.TotalSpeedMbps,
                ActiveCount: overview.Summary.ActiveCount,
                UploadMbps: overview.Summary.TotalUploadSpeedMbps),
            cancellationToken);

        if (now - _lastPruneUtc < PruneInterval)
        {
            return interval;
        }

        _lastPruneUtc = now;
        var removed = await repository.PruneAsync(now - Retention, cancellationToken);
        if (removed > 0)
        {
            logger.LogDebug("Pruned {Count} download throughput samples past retention.", removed);
        }

        return interval;
    }
}
