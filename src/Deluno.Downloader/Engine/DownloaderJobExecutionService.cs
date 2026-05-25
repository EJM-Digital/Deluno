using Deluno.Downloader.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Downloader.Engine;

/// <summary>
/// The thing that makes the built-in engine actually run.
///
/// Polls <c>downloader.jobs</c> for queued items, picks the right
/// per-protocol <see cref="IDownloaderJobExecutor"/>, and runs each
/// job to PostProcessed (or Failed). One in-flight job at a time per
/// protocol — concurrency is bounded inside each executor (NNTP
/// connection pool size, MonoTorrent's own scheduler).
///
/// Polling interval: 5s. Could be reduced to event-driven (we already
/// have a job-state change channel) but the polling shape is the
/// simplest correct thing — the worker has bounded work and the
/// poll cost is negligible.
/// </summary>
public sealed class DownloaderJobExecutionService(
    IJobRepository jobs,
    IEnumerable<IDownloaderJobExecutor> executors,
    ILogger<DownloaderJobExecutionService> logger)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var executorByProtocol = executors.ToDictionary(e => e.Protocol);
        logger.LogInformation(
            "Downloader job execution service started; protocols handled: {Protocols}.",
            string.Join(",", executorByProtocol.Keys));

        // Per-protocol concurrency latch so two jobs of the same protocol
        // don't try to share the same connection pool / engine instance
        // out from under each other.
        var locks = executorByProtocol.Keys
            .ToDictionary(p => p, _ => new SemaphoreSlim(1, 1));

        using var timer = new PeriodicTimer(PollInterval);
        while (await SafeWaitForNextTickAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var queued = await jobs.ListPriorityOrderedAsync(JobLifecycleState.Queued, limit: 16, stoppingToken);
                foreach (var job in queued)
                {
                    if (!executorByProtocol.TryGetValue(job.Protocol, out var executor))
                    {
                        logger.LogWarning(
                            "No executor for protocol {Protocol} on job {JobId}; leaving queued.",
                            job.Protocol, job.Id);
                        continue;
                    }

                    // Per-protocol latch — non-blocking so a stuck NZB
                    // job doesn't prevent a queued torrent from
                    // starting.
                    if (!await locks[job.Protocol].WaitAsync(0, stoppingToken).ConfigureAwait(false))
                        continue;

                    _ = ExecuteOneAsync(executor, job, locks[job.Protocol], stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Downloader execution poll cycle failed; continuing.");
            }
        }
    }

    private async Task ExecuteOneAsync(
        IDownloaderJobExecutor executor, JobRecord job, SemaphoreSlim latch, CancellationToken ct)
    {
        try
        {
            await executor.ExecuteAsync(job, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Executor for {Protocol} job {JobId} crashed outside the executor's own catch.",
                executor.Protocol, job.Id);
        }
        finally
        {
            latch.Release();
        }
    }

    private static async Task<bool> SafeWaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }
}
