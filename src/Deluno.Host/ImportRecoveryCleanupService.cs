using Deluno.Movies.Data;
using Deluno.Series.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Host;

internal sealed class ImportRecoveryCleanupService(
    IMovieCatalogRepository movieRepository,
    ISeriesCatalogRepository seriesRepository,
    TimeProvider timeProvider,
    ILogger<ImportRecoveryCleanupService> logger)
    : BackgroundService
{
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CleanupInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCleanupAsync(stoppingToken);
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
        var cutoff = timeProvider.GetUtcNow() - RetentionPeriod;

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
}
