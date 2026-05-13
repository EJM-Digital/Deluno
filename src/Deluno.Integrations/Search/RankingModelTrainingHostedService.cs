using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Integrations.Search;

public sealed class RankingModelTrainingHostedService(
    IReleaseRankingModelService rankingModelService,
    IReleaseRankingModelAdminService rankingModelAdminService,
    IConfiguration configuration,
    ILogger<RankingModelTrainingHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var status = rankingModelService.GetStatus();
        if (!status.Enabled)
        {
            logger.LogInformation("Ranking model is disabled. Scheduled retraining will not run.");
            return;
        }

        var runOnStartup = configuration.GetValue("Deluno:RankingModel:TrainOnStartup", true);
        if (runOnStartup)
        {
            await RunTrainingAsync("startup", stoppingToken);
        }

        var intervalHours = Math.Clamp(configuration.GetValue("Deluno:RankingModel:RetrainIntervalHours", 24), 1, 168);
        var interval = TimeSpan.FromHours(intervalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            await RunTrainingAsync("scheduled", stoppingToken);
        }
    }

    private async Task RunTrainingAsync(string reason, CancellationToken cancellationToken)
    {
        try
        {
            var result = await rankingModelAdminService.TrainAsync(reason, cancellationToken);
            if (result.Success)
            {
                logger.LogInformation(
                    "Ranking model training succeeded. Version={Version} Samples={Samples} AUC={Auc:0.###} Accuracy={Accuracy:0.###}",
                    result.ModelVersion,
                    result.SampleCount,
                    result.Auc ?? 0,
                    result.Accuracy ?? 0);
            }
            else
            {
                logger.LogInformation(
                    "Ranking model training skipped/failed: {Message} (Samples={Samples})",
                    result.Message,
                    result.SampleCount);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ranking model scheduled training failed.");
        }
    }
}
