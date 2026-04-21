using Deluno.Jobs.Data;
using Deluno.Platform.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Deluno.Worker.Services;

public sealed class DelunoHeartbeatWorker(
    ILogger<DelunoHeartbeatWorker> logger,
    IJobQueueRepository jobQueueRepository,
    IPlatformSettingsRepository platformSettingsRepository)
    : BackgroundService
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _workerId = $"worker-{Environment.MachineName.ToLowerInvariant()}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        logger.LogInformation("Deluno worker runtime started as {WorkerId}.", _workerId);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await jobQueueRepository.HeartbeatAsync(_workerId, stoppingToken);

            var settings = await platformSettingsRepository.GetAsync(stoppingToken);
            if (!settings.AutoStartJobs)
            {
                logger.LogDebug("Worker {WorkerId} heartbeat tick with auto-start disabled.", _workerId);
                continue;
            }

            var libraries = await platformSettingsRepository.ListLibrariesAsync(stoppingToken);
            var automationPlans = libraries
                .Select(library => new Deluno.Jobs.Contracts.LibraryAutomationPlanItem(
                    LibraryId: library.Id,
                    LibraryName: library.Name,
                    MediaType: library.MediaType,
                    AutoSearchEnabled: library.AutoSearchEnabled,
                    MissingSearchEnabled: library.MissingSearchEnabled,
                    UpgradeSearchEnabled: library.UpgradeSearchEnabled,
                    SearchIntervalHours: library.SearchIntervalHours,
                    RetryDelayHours: library.RetryDelayHours,
                    MaxItemsPerRun: library.MaxItemsPerRun))
                .ToArray();

            await jobQueueRepository.PlanLibrarySearchesAsync(automationPlans, stoppingToken);

            var job = await jobQueueRepository.LeaseNextAsync(
                _workerId,
                TimeSpan.FromMinutes(2),
                stoppingToken);

            if (job is null)
            {
                logger.LogDebug("Worker {WorkerId} heartbeat tick with no pending jobs.", _workerId);
                continue;
            }

            try
            {
                logger.LogInformation("Processing job {JobId} of type {JobType}.", job.Id, job.JobType);
                await Task.Delay(TimeSpan.FromMilliseconds(800), stoppingToken);

                var message = BuildCompletionMessage(job);

                await jobQueueRepository.CompleteAsync(job.Id, _workerId, message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker {WorkerId} failed processing job {JobId}.", _workerId, job.Id);
                await jobQueueRepository.FailAsync(job.Id, _workerId, ex.Message, stoppingToken);
            }
        }
    }

    private static string BuildCompletionMessage(Deluno.Jobs.Contracts.JobQueueItem job)
    {
        if (job.JobType == "library.search")
        {
            try
            {
                var payload = JsonSerializer.Deserialize<LibrarySearchPayload>(job.PayloadJson ?? "{}", PayloadJsonOptions);
                if (payload is not null && !string.IsNullOrWhiteSpace(payload.LibraryName))
                {
                    if (payload.CheckMissing && payload.CheckUpgrades)
                    {
                        return $"Finished checking {payload.LibraryName} for missing and better releases.";
                    }

                    if (payload.CheckMissing)
                    {
                        return $"Finished checking {payload.LibraryName} for missing releases.";
                    }

                    if (payload.CheckUpgrades)
                    {
                        return $"Finished checking {payload.LibraryName} for better releases.";
                    }

                    return $"Finished checking {payload.LibraryName}.";
                }
            }
            catch
            {
                return "Finished checking a library.";
            }
        }

        return job.JobType switch
        {
            "movies.catalog.refresh" => "Finished checking your movie library.",
            "series.catalog.refresh" => "Finished checking your TV show library.",
            _ => "Finished a background task."
        };
    }

    private sealed record LibrarySearchPayload(
        string LibraryId,
        string LibraryName,
        string MediaType,
        bool CheckMissing,
        bool CheckUpgrades,
        int MaxItems,
        int RetryDelayHours,
        string TriggeredBy);
}
