using Deluno.Contracts;
using Deluno.Jobs.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.HealthChecks;

/// <summary>
/// Runs every health check on the fifteen-minute <c>SystemTasks.HealthChecks</c>
/// clock, and picks up an immediate re-check request the moment a setting a
/// check depends on changes (#909).
///
/// <para>The tick itself is short -- every fifteen seconds -- but almost every
/// tick does nothing: <see cref="IJobQueueRepository.TryClaimScheduledPassAsync"/>
/// only lets the full pass through once every fifteen minutes, the same
/// shared-heartbeat claim every other scheduled pass uses. The short tick
/// exists only so <see cref="IHealthCheckRepository.TakePendingImmediateRunsAsync"/>
/// is checked often enough that "run right after it changes" means seconds,
/// not up to fifteen minutes.</para>
/// </summary>
public sealed class HealthCheckHostedService(
    ILogger<HealthCheckHostedService> logger,
    IServiceScopeFactory scopeFactory,
    IHealthCheckRepository repository,
    IJobQueueRepository jobQueueRepository,
    TimeProvider timeProvider)
    : BackgroundService
{
    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Health check service started; the full pass runs every {Interval}, checked every {Tick}.",
            SystemTasks.IntervalFor(SystemTasks.HealthChecks),
            TickInterval);

        try
        {
            await Task.Delay(StartupDelay, timeProvider, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TickInterval, timeProvider);
        do
        {
            try
            {
                await RunPendingImmediateChecksAsync(stoppingToken);
                await RunScheduledPassAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Error occurred while running health checks.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunPendingImmediateChecksAsync(CancellationToken cancellationToken)
    {
        var pending = await repository.TakePendingImmediateRunsAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return;
        }

        logger.LogInformation("Running health checks for {Categories} now, requested by a setting change.", string.Join(", ", pending));
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<HealthCheckRunner>().RunCategoriesAsync(pending, cancellationToken);
    }

    private async Task RunScheduledPassAsync(CancellationToken stoppingToken)
    {
        if (!await jobQueueRepository.TryClaimScheduledPassAsync(
                SystemTasks.HealthChecks,
                SystemTasks.IntervalFor(SystemTasks.HealthChecks),
                stoppingToken))
        {
            return;
        }

        var startedUtc = timeProvider.GetUtcNow();
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<HealthCheckRunner>().RunAllAsync(stoppingToken);
            await RecordOutcomeAsync(startedUtc, "completed", CancellationToken.None);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await RecordOutcomeAsync(startedUtc, "cancelled", CancellationToken.None);
        }
        catch (Exception exception)
        {
            await RecordOutcomeAsync(startedUtc, "failed", CancellationToken.None);
            logger.LogError(exception, "Error occurred during the scheduled health check pass.");
        }
    }

    private async Task RecordOutcomeAsync(DateTimeOffset startedUtc, string result, CancellationToken cancellationToken)
    {
        var completedUtc = timeProvider.GetUtcNow();
        await jobQueueRepository.RecordScheduledPassOutcomeAsync(
            SystemTasks.HealthChecks,
            completedUtc,
            result,
            Math.Max(0, (long)(completedUtc - startedUtc).TotalMilliseconds),
            completedUtc.Add(SystemTasks.IntervalFor(SystemTasks.HealthChecks)),
            cancellationToken);
    }
}
