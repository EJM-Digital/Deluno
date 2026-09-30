using Deluno.Contracts;
using Deluno.Jobs.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Series.Services;

/// <summary>
/// Schedules <see cref="ISceneMappingRefreshService"/> on its own declared
/// cadence (#863). Mirrors <c>DownloadDispatchPollingHostedService</c>: a short
/// check interval decides how often the pass is <i>considered</i>, and
/// <see cref="IJobQueueRepository.TryClaimScheduledPassAsync"/> decides whether
/// it is actually due, so a restart never resets the clock.
/// </summary>
public sealed class SceneMappingRefreshHostedService(
    ISceneMappingRefreshService refreshService,
    IJobQueueRepository jobQueueRepository,
    ILogger<SceneMappingRefreshHostedService> logger,
    TimeProvider timeProvider)
    : BackgroundService
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, timeProvider, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(CheckInterval, timeProvider);
        do
        {
            try
            {
                if (!await jobQueueRepository.TryClaimScheduledPassAsync(
                        SystemTasks.SceneMappingRefresh,
                        SystemTasks.IntervalFor(SystemTasks.SceneMappingRefresh),
                        stoppingToken))
                {
                    continue;
                }

                var startedUtc = timeProvider.GetUtcNow();
                try
                {
                    var summary = await refreshService.RefreshAllAsync(stoppingToken);
                    logger.LogInformation(
                        "Scene mapping refresh completed: {Checked} anime titles checked, {WithoutTvdb} without a TVDB id, {Corrected} with a scene correction, {Episodes} episodes updated.",
                        summary.AnimeSeriesChecked,
                        summary.SeriesWithoutTvDbId,
                        summary.SeriesWithCorrections,
                        summary.EpisodesUpdated);
                    await RecordOutcomeAsync(startedUtc, "completed", stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    await RecordOutcomeAsync(startedUtc, "cancelled", CancellationToken.None);
                }
                catch (Exception exception)
                {
                    await RecordOutcomeAsync(startedUtc, "failed", CancellationToken.None);
                    logger.LogWarning(exception, "Scene mapping refresh failed.");
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Error occurred while scheduling the scene mapping refresh.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RecordOutcomeAsync(DateTimeOffset startedUtc, string result, CancellationToken cancellationToken)
    {
        var completedUtc = timeProvider.GetUtcNow();
        await jobQueueRepository.RecordScheduledPassOutcomeAsync(
            SystemTasks.SceneMappingRefresh,
            completedUtc,
            result,
            Math.Max(0, (long)(completedUtc - startedUtc).TotalMilliseconds),
            completedUtc.Add(SystemTasks.IntervalFor(SystemTasks.SceneMappingRefresh)),
            cancellationToken);
    }
}
