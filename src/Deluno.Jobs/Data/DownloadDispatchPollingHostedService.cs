using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Jobs.Data;

public sealed class DownloadDispatchPollingHostedService(
    ILogger<DownloadDispatchPollingHostedService> logger,
    IDownloadDispatchPollingService pollingService)
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
                var report = await pollingService.PollAsync(stoppingToken);
                logger.LogInformation(
                    "Download dispatch polling completed: {UnresolvedChecked} unresolved, {GrabTimeouts} grab timeouts, {DetectionTimeouts} detection timeouts, {ImportTimeouts} import timeouts, {ImportFailures} import failures, {RecoveryCases} recovery cases recorded.",
                    report.UnresolvedDispatchesChecked,
                    report.GrabTimeoutsDetected,
                    report.DetectionTimeoutsDetected,
                    report.ImportTimeoutsDetected,
                    report.ImportFailuresDetected,
                    report.RecoveryCasesRecorded);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred during download dispatch polling.");
            }
        }
    }
}
