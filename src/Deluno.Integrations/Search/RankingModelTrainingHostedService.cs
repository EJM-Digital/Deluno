using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Deluno.Contracts;
using Deluno.Jobs.Data;

namespace Deluno.Integrations.Search;

public sealed class RankingModelTrainingHostedService(
    IReleaseRankingModelService rankingModelService,
    IReleaseRankingModelAdminService rankingModelAdminService,
    IConfiguration configuration,
    ILogger<RankingModelTrainingHostedService> logger,
    IJobQueueRepository jobQueueRepository,
    TimeProvider timeProvider)
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
            await RunTrackedTrainingAsync("startup", intervalHours: null, cancellationToken: stoppingToken);
        }

        var intervalHours = Math.Clamp(configuration.GetValue("Deluno:RankingModel:RetrainIntervalHours", 24), 1, 168);
        var interval = SystemTasks.IntervalForHours(SystemTasks.RankingModelTraining, intervalHours);

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

            await RunTrackedTrainingAsync("scheduled", intervalHours, stoppingToken);
        }
    }

    private async Task RunTrackedTrainingAsync(
        string reason,
        int? intervalHours,
        CancellationToken cancellationToken)
    {
        var hours = intervalHours ?? Math.Clamp(configuration.GetValue("Deluno:RankingModel:RetrainIntervalHours", 24), 1, 168);
        if (!await jobQueueRepository.TryClaimScheduledPassAsync(
                SystemTasks.RankingModelTraining,
                SystemTasks.IntervalForHours(SystemTasks.RankingModelTraining, hours),
                cancellationToken))
        {
            return;
        }

        var startedUtc = timeProvider.GetUtcNow();
        try
        {
            var result = await RunTrainingAsync(reason, cancellationToken);
            await jobQueueRepository.RecordScheduledPassOutcomeAsync(
                SystemTasks.RankingModelTraining,
                timeProvider.GetUtcNow(),
                result ? "completed" : "skipped",
                Math.Max(0, (long)(timeProvider.GetUtcNow() - startedUtc).TotalMilliseconds),
                startedUtc.Add(SystemTasks.IntervalForHours(SystemTasks.RankingModelTraining, hours)),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await jobQueueRepository.RecordScheduledPassOutcomeAsync(
                SystemTasks.RankingModelTraining,
                timeProvider.GetUtcNow(),
                "cancelled",
                Math.Max(0, (long)(timeProvider.GetUtcNow() - startedUtc).TotalMilliseconds),
                null,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            await jobQueueRepository.RecordScheduledPassOutcomeAsync(
                SystemTasks.RankingModelTraining,
                timeProvider.GetUtcNow(),
                "failed",
                Math.Max(0, (long)(timeProvider.GetUtcNow() - startedUtc).TotalMilliseconds),
                null,
                CancellationToken.None);
            logger.LogWarning(exception, "Ranking model scheduled training failed.");
        }
    }

    private async Task<bool> RunTrainingAsync(string reason, CancellationToken cancellationToken)
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
                return true;
            }
            else
            {
                logger.LogInformation(
                    "Ranking model training skipped/failed: {Message} (Samples={Samples})",
                    result.Message,
                    result.SampleCount);
                return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ranking model scheduled training failed.");
            return false;
        }
    }
}
