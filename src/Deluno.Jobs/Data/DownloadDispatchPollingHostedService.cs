using Deluno.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Jobs.Data;

public sealed class DownloadDispatchPollingHostedService(
    ILogger<DownloadDispatchPollingHostedService> logger,
    IDownloadDispatchPollingService pollingService,
    IJobQueueRepository jobQueueRepository,
    TimeProvider timeProvider)
    : BackgroundService
{
    /// <summary>
    /// How often the pass is considered, not how often it runs (#601). It runs
    /// when its persisted interval says it is due, so a short tick costs one
    /// read. It was an hour-long timer whose first tick came an hour after
    /// start: on the soak rig Deluno was redeployed about ten times in a day,
    /// each within the hour, and the pass that archives imported downloads and
    /// finds stuck ones never ran.
    /// </summary>
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>A moment after start, so the first check does not race the rest of startup.</summary>
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Download dispatch polling service started; it runs every {Interval}, checked every {Check}.",
            SystemTasks.IntervalFor(SystemTasks.DownloadDispatchPolling),
            CheckInterval);

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
                        SystemTasks.DownloadDispatchPolling,
                        SystemTasks.IntervalFor(SystemTasks.DownloadDispatchPolling),
                        stoppingToken))
                {
                    continue;
                }

                var startedUtc = timeProvider.GetUtcNow();
                try
                {
                    var report = await pollingService.PollAsync(stoppingToken);
                    logger.LogInformation(
                        "Download dispatch polling completed: {UnresolvedChecked} unresolved, {GrabTimeouts} grab timeouts, {DetectionTimeouts} detection timeouts, {ImportTimeouts} import timeouts, {ImportFailures} import failures, {RecoveryCases} recovery cases recorded, {AlertsResolved} alerts resolved.",
                        report.UnresolvedDispatchesChecked,
                        report.GrabTimeoutsDetected,
                        report.DetectionTimeoutsDetected,
                        report.ImportTimeoutsDetected,
                        report.ImportFailuresDetected,
                        report.RecoveryCasesRecorded,
                        report.AlertsResolved);
                    await RecordOutcomeAsync(startedUtc, "completed", stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    await RecordOutcomeAsync(startedUtc, "cancelled", CancellationToken.None);
                }
                catch (Exception exception)
                {
                    await RecordOutcomeAsync(startedUtc, "failed", CancellationToken.None);
                    logger.LogError(exception, "Error occurred during download dispatch polling.");
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Error occurred while scheduling download dispatch polling.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RecordOutcomeAsync(
        DateTimeOffset startedUtc,
        string result,
        CancellationToken cancellationToken)
    {
        var completedUtc = timeProvider.GetUtcNow();
        await jobQueueRepository.RecordScheduledPassOutcomeAsync(
            SystemTasks.DownloadDispatchPolling,
            completedUtc,
            result,
            Math.Max(0, (long)(completedUtc - startedUtc).TotalMilliseconds),
            completedUtc.Add(SystemTasks.IntervalFor(SystemTasks.DownloadDispatchPolling)),
            cancellationToken);
    }
}
