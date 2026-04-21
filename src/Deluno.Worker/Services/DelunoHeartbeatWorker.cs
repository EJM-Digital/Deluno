using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Services;

public sealed class DelunoHeartbeatWorker(ILogger<DelunoHeartbeatWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        logger.LogInformation("Deluno worker runtime started.");

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            logger.LogInformation("Deluno worker heartbeat tick.");
        }
    }
}

