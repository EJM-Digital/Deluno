using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Connections;

/// <summary>
/// The regular, real test of every connection (#1098): an indexer, download client, processor, media server,
/// Seerr, metadata service or list provider that no real call has used for fifteen minutes is tested, so the
/// "last checked" System shows is always honest. It only reads, so it always runs (#1432). The Activity feed hears only when a result changes.
/// </summary>
public sealed class RegularConnectionCheckService(
    ILogger<RegularConnectionCheckService> logger,
    IServiceScopeFactory scopeFactory,
    IJobQueueRepository jobQueueRepository,
    TimeProvider timeProvider)
    : BackgroundService
{
    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

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

        using var timer = new PeriodicTimer(TickInterval, timeProvider);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Error occurred while testing connections.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One look: does nothing when another pass already ran this minute.</summary>
    internal async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        if (!await jobQueueRepository.TryClaimScheduledPassAsync(
                SystemTasks.ConnectionsCheck,
                SystemTasks.IntervalFor(SystemTasks.ConnectionsCheck),
                stoppingToken))
        {
            return;
        }

        var startedUtc = timeProvider.GetUtcNow();
        var result = "completed";
        try
        {
            await scope.ServiceProvider.GetRequiredService<IConnectionHealthTester>()
                .TestStaleAsync(startedUtc - ConnectionCheckRules.RegularInterval, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            result = "cancelled";
        }
        catch (Exception exception)
        {
            result = "failed";
            logger.LogError(exception, "Error occurred during the regular connection check.");
        }

        var completedUtc = timeProvider.GetUtcNow();
        await jobQueueRepository.RecordScheduledPassOutcomeAsync(
            SystemTasks.ConnectionsCheck,
            completedUtc,
            result,
            Math.Max(0, (long)(completedUtc - startedUtc).TotalMilliseconds),
            completedUtc.Add(SystemTasks.IntervalFor(SystemTasks.ConnectionsCheck)),
            CancellationToken.None);
    }
}
