using Deluno.Jobs.Data;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Services;

/// <summary>
/// Prunes Deluno's own event/strike history - Activity feed rows and
/// download-health strike/progress records - to the person's configured
/// window (#828), once a day. A different concept entirely from
/// RetentionService, which decides when media titles themselves are removed
/// from the library: this never touches a title, a file, or anything but
/// Deluno's own record of what happened and when. Without it, history grows
/// forever (each row was already excluded from a listing past 90 days, but
/// never actually deleted) or is pruned on a hidden schedule nobody chose.
/// </summary>
public sealed class HistoryRetentionWorker(
    ILogger<HistoryRetentionWorker> logger,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Every install's first hour still gets a pass, rather than waiting a
        // full day after every restart before ever pruning anything.
        var wait = TimeSpan.FromMinutes(5);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(wait, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await PruneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "History retention pruning failed; it will try again on the next pass.");
            }

            wait = Interval;
        }
    }

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var settingsRepository = scope.ServiceProvider.GetRequiredService<IPlatformSettingsRepository>();
        var activityFeedRepository = scope.ServiceProvider.GetRequiredService<IActivityFeedRepository>();
        var healthRepository = scope.ServiceProvider.GetRequiredService<IDownloadHealthRepository>();

        var settings = await settingsRepository.GetAsync(cancellationToken);
        var retentionDays = Math.Clamp(settings.HistoryRetentionDays, 1, 3650);
        var olderThan = timeProvider.GetUtcNow() - TimeSpan.FromDays(retentionDays);

        var prunedActivity = await activityFeedRepository.PruneActivityAsync(olderThan, cancellationToken);
        var prunedHealth = await healthRepository.PruneDownloadHealthHistoryAsync(olderThan, cancellationToken);

        if (prunedActivity > 0 || prunedHealth > 0)
        {
            logger.LogInformation(
                "History retention pruned {ActivityRows} Activity row(s) and {HealthRows} download-health history row(s) past {RetentionDays} day(s).",
                prunedActivity, prunedHealth, retentionDays);
        }
    }
}
