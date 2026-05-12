using Deluno.Movies.Data;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Host;

internal sealed class ImportRecoveryCleanupService(
    IMovieCatalogRepository movieRepository,
    ISeriesCatalogRepository seriesRepository,
    IPlatformSettingsRepository platformSettingsRepository,
    TimeProvider timeProvider,
    ILogger<ImportRecoveryCleanupService> logger)
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
}
