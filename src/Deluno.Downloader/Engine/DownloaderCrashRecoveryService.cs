using Deluno.Downloader.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Downloader.Engine;

/// <summary>
/// Mid-flight crash recovery. Runs once at startup BEFORE the regular
/// execution worker starts pulling Queued items.
///
/// Problem: if the process dies during Fetching / Verify / Extracting /
/// PostProcessed, the job's row stays in that state forever. Subsequent
/// runs of the execution worker only pull Queued, so the half-finished
/// job is orphaned.
///
/// Recovery policy: at startup, any non-terminal job that isn't
/// Queued, Paused, or Done/Failed gets transitioned back to Queued
/// with a "recovered from crash" reason. The next worker tick picks
/// it up and starts over.
///
/// Why "start over" instead of "resume mid-flight": the spike showed
/// that mid-NZB-download resume requires tracking per-segment state
/// (which articles are on disk, which still need fetching, the file's
/// pre-allocated layout intact). That's substantial work; for now
/// we accept the wasted bytes and re-fetch from the top. Future
/// optimization: torrent jobs resume natively via MonoTorrent's
/// fast-resume blob — that path is already in the schema
/// (torrent_metadata.fast_resume_blob) but not yet wired.
/// </summary>
public sealed class DownloaderCrashRecoveryService(
    IJobRepository jobs,
    TimeProvider time,
    ILogger<DownloaderCrashRecoveryService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Every non-terminal "in flight" state. Paused stays Paused
        // (user intent); Queued stays Queued (will be picked up); Done
        // and Failed stay terminal.
        var midFlight = new[]
        {
            JobLifecycleState.Fetching,
            JobLifecycleState.Reassembled,
            JobLifecycleState.Verify,
            JobLifecycleState.Verified,
            JobLifecycleState.Repair,
            JobLifecycleState.Extracting,
            JobLifecycleState.Extracted,
            JobLifecycleState.PostProcessed,
            JobLifecycleState.ImportPending,
            JobLifecycleState.Seeding,
        };

        try
        {
            var orphaned = await jobs.ListByStateAsync(midFlight, limit: 1024, cancellationToken);
            if (orphaned.Count == 0) return;

            logger.LogWarning(
                "Crash recovery: found {Count} mid-flight job(s) from a previous run. Re-queueing.",
                orphaned.Count);

            foreach (var job in orphaned)
            {
                try
                {
                    await jobs.TransitionAsync(
                        job.Id,
                        JobLifecycleState.Queued,
                        $"Recovered from crash (was {job.State}).",
                        time.GetUtcNow(),
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Illegal transition from a state we missed in the
                    // mid-flight list, or persistence error. Log and
                    // move on — the job stays where it is and a human
                    // can intervene via the jobs API / Settings UI.
                    logger.LogError(ex, "Could not re-queue orphaned job {JobId} from state {State}.",
                        job.Id, job.State);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Crash recovery sweep failed; continuing without recovery.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
