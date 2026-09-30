using Deluno.Contracts;
using Deluno.Jobs.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Series.Services;

/// <summary>
/// Schedules <see cref="IEpisodeGroupRefreshService"/> on its own declared
/// cadence (#863). Mirrors <see cref="SceneMappingRefreshHostedService"/>: a
/// short check interval decides how often the pass is <i>considered</i>, and
/// <see cref="IJobQueueRepository.TryClaimScheduledPassAsync"/> decides
/// whether it is actually due, so a restart never resets the clock. Unlike
/// that one, this resolves its refresh service from a fresh scope per pass
/// (the same way <c>HealthCheckHostedService</c> does), because
/// <see cref="IEpisodeGroupRefreshService"/> consumes the scoped
/// <c>IMetadataProvider</c>.
/// </summary>
public sealed class EpisodeGroupRefreshHostedService(
    IServiceScopeFactory scopeFactory,
    IJobQueueRepository jobQueueRepository,
    ILogger<EpisodeGroupRefreshHostedService> logger,
    TimeProvider timeProvider)
    : BackgroundService
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);

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
                        SystemTasks.EpisodeGroupRefresh,
                        SystemTasks.IntervalFor(SystemTasks.EpisodeGroupRefresh),
                        stoppingToken))
                {
                    continue;
                }

                var startedUtc = timeProvider.GetUtcNow();
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var refreshService = scope.ServiceProvider.GetRequiredService<IEpisodeGroupRefreshService>();
                    var summary = await refreshService.RefreshAllAsync(stoppingToken);
                    logger.LogInformation(
                        "Episode-group refresh completed: {Checked} TMDb titles checked, {WithoutId} without a TMDb id, {Updated} with ordering updates, {Episodes} episodes updated.",
                        summary.SeriesChecked,
                        summary.SeriesWithoutProviderId,
                        summary.SeriesWithUpdatedOrderings,
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
                    logger.LogWarning(exception, "Episode-group refresh failed.");
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Error occurred while scheduling the episode-group refresh.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RecordOutcomeAsync(DateTimeOffset startedUtc, string result, CancellationToken cancellationToken)
    {
        var completedUtc = timeProvider.GetUtcNow();
        await jobQueueRepository.RecordScheduledPassOutcomeAsync(
            SystemTasks.EpisodeGroupRefresh,
            completedUtc,
            result,
            Math.Max(0, (long)(completedUtc - startedUtc).TotalMilliseconds),
            completedUtc.Add(SystemTasks.IntervalFor(SystemTasks.EpisodeGroupRefresh)),
            cancellationToken);
    }
}
