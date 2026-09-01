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
    private static readonly TimeSpan PollingInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Download dispatch polling service started with interval {Interval}.", PollingInterval);

        using var timer = new PeriodicTimer(PollingInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
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
                        "Download dispatch polling completed: {UnresolvedChecked} unresolved, {GrabTimeouts} grab timeouts, {DetectionTimeouts} detection timeouts, {ImportTimeouts} import timeouts, {ImportFailures} import failures, {RecoveryCases} recovery cases recorded.",
                        report.UnresolvedDispatchesChecked,
                        report.GrabTimeoutsDetected,
                        report.DetectionTimeoutsDetected,
                        report.ImportTimeoutsDetected,
                        report.ImportFailuresDetected,
                        report.RecoveryCasesRecorded);
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error occurred while scheduling download dispatch polling.");
            }
        }
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
