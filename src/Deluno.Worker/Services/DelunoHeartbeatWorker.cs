using Deluno.Jobs.Data;
using Deluno.Platform.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Services;

public sealed class DelunoHeartbeatWorker(
    ILogger<DelunoHeartbeatWorker> logger,
    IJobQueueRepository jobQueueRepository,
    IPlatformSettingsRepository platformSettingsRepository)
    : BackgroundService
{
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

                var message = job.JobType switch
                {
                    "movies.catalog.refresh" => "Movie follow-up job completed.",
                    "series.catalog.refresh" => "Series follow-up job completed.",
                    _ => $"{job.JobType} completed."
                };

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
}
