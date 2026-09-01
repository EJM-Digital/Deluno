using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Quality.Guides;

/// <summary>
/// Performs an opt-in check at most weekly. It waits for the schema gate and
/// never prevents Deluno from starting if the public guide host is unavailable.
/// </summary>
public sealed class GuideUpdateCheckHostedService(
    IGuideUpdateCheckService updateCheckService,
    IDelunoStartupGate startupGate,
    ILogger<GuideUpdateCheckHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(12);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await startupGate.WaitAsync(TimeSpan.FromMinutes(2), stoppingToken);
            await CheckIfDueAsync(stoppingToken);
            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await CheckIfDueAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task CheckIfDueAsync(CancellationToken cancellationToken)
    {
        try
        {
            await updateCheckService.RunIfDueAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "The opt-in TRaSH Guides update check could not run.");
        }
    }
}
