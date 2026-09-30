using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// Runs the tracker-definition update (#756) at most once per the owner's
/// configured interval, and only while the switch is on -- off by default
/// (#936), so a brand-new install never fetches anything on its own until a
/// person turns it on in settings.
/// </summary>
public sealed class TrackerDefinitionAutoUpdateHostedService(
    ITrackerDefinitionUpdateService updateService,
    IDelunoStartupGate startupGate,
    ILogger<TrackerDefinitionAutoUpdateHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!await startupGate.WaitUntilReadyAsync(logger, "The tracker-definition update", stoppingToken))
            {
                return;
            }
            await RunIfDueAsync(stoppingToken);
            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunIfDueAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task RunIfDueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await updateService.RunIfDueAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "The scheduled tracker-definition update could not run.");
        }
    }
}
