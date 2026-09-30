using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;

namespace Deluno.Integrations.Search;

public sealed class RankingModelTrainingHostedService(
    IReleaseRankingModelService rankingModelService,
    IReleaseRankingModelAdminService rankingModelAdminService,
    IConfiguration configuration,
    ILogger<RankingModelTrainingHostedService> logger,
    IJobQueueRepository jobQueueRepository,
    TimeProvider timeProvider,
    IDelunoStartupGate startupGate)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var status = rankingModelService.GetStatus();
            if (!status.Enabled)
            {
                logger.LogInformation("Ranking model is disabled. Scheduled retraining will not run.");
                return;
            }
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            // Reading the status is not worth ending the service for (#1080); carry on and train.
            logger.LogWarning(exception, "Could not read the ranking model status; scheduled retraining carries on.");
        }

        // The retraining claim writes to the jobs database, which may still be
        // being prepared right after start-up.
        if (!await startupGate.WaitUntilReadyAsync(logger, "Ranking model training", stoppingToken))
        {
            return;
        }

        var runOnStartup = configuration.GetValue("Deluno:RankingModel:TrainOnStartup", true);
        if (runOnStartup)
        {
            await RunGuardedAsync("startup", null, stoppingToken);
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

            await RunGuardedAsync("scheduled", intervalHours, stoppingToken);
        }
    }

    // A failed pass (or a failure recording it) must not end the loop, or the
    // whole host: log it and wait for the next interval (#1080).
    private async Task RunGuardedAsync(string reason, int? intervalHours, CancellationToken cancellationToken)
    {
        try
        {
            await RunTrackedTrainingAsync(reason, intervalHours, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "The {Reason} ranking-model training pass failed; it will try again at the next interval.", reason);
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
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Ranking model scheduled training failed.");
            return false;
        }
    }
}
