using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Platform.Data;
using Deluno.Recovery.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Recovery.Services;

public sealed class ImportRecoveryRetentionService(
    IMovieImportRecoveryRetentionRepository movieRepository,
    ISeriesImportRecoveryRetentionRepository seriesRepository,
    IPlatformSettingsRepository platformSettingsRepository,
    TimeProvider timeProvider,
    ILogger<ImportRecoveryRetentionService> logger,
    IJobQueueRepository jobQueueRepository)
    : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CleanupInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await jobQueueRepository.TryClaimScheduledPassAsync(
                        SystemTasks.ImportRecoveryRetention,
                        SystemTasks.IntervalFor(SystemTasks.ImportRecoveryRetention),
                        stoppingToken))
                {
                    var startedUtc = timeProvider.GetUtcNow();
                    try
                    {
                        await RunCleanupAsync(stoppingToken);
                        await RecordOutcomeAsync(startedUtc, "completed", stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        await RecordOutcomeAsync(startedUtc, "cancelled", CancellationToken.None);
                    }
                    catch (Exception exception)
                    {
                        await RecordOutcomeAsync(startedUtc, "failed", CancellationToken.None);
                        logger.LogWarning(exception, "Import recovery cleanup encountered an error.");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Import recovery cleanup encountered an error.");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunCleanupAsync(CancellationToken cancellationToken)
    {
        var settings = await platformSettingsRepository.GetAsync(cancellationToken);
        var retentionDays = settings.ImportRecoveryRetentionDays > 0 ? settings.ImportRecoveryRetentionDays : 30;
        var cutoff = timeProvider.GetUtcNow() - TimeSpan.FromDays(retentionDays);

        var movieCount = await movieRepository.CleanupImportRecoveryCasesAsync(cutoff, cancellationToken);
        var seriesCount = await seriesRepository.CleanupImportRecoveryCasesAsync(cutoff, cancellationToken);

        if (movieCount > 0 || seriesCount > 0)
        {
            logger.LogInformation(
                "Import recovery cleanup removed {MovieCount} movie cases and {SeriesCount} series cases resolved before {Cutoff:O}.",
                movieCount,
                seriesCount,
                cutoff);
        }
    }

    private async Task RecordOutcomeAsync(
        DateTimeOffset startedUtc,
        string result,
        CancellationToken cancellationToken)
    {
        var completedUtc = timeProvider.GetUtcNow();
        await jobQueueRepository.RecordScheduledPassOutcomeAsync(
            SystemTasks.ImportRecoveryRetention,
            completedUtc,
            result,
            Math.Max(0, (long)(completedUtc - startedUtc).TotalMilliseconds),
            startedUtc.Add(SystemTasks.IntervalFor(SystemTasks.ImportRecoveryRetention)),
            cancellationToken);
    }
}
