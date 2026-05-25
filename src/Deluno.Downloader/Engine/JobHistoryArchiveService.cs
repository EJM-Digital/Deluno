using Deluno.Downloader.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Downloader.Engine;

/// <summary>
/// Periodically archives jobs that have reached a terminal state
/// (<see cref="JobLifecycleState.Done"/> or <see cref="JobLifecycleState.Failed"/>)
/// into the <c>history</c> table.
///
/// <para><b>Why a separate hosted service:</b> the executors hand off
/// at <see cref="JobLifecycleState.PostProcessed"/> — the
/// <c>ImportPending → Done</c> tail is driven by the existing telemetry
/// /import chain in <c>Deluno.Integrations</c>, which runs in a
/// different process boundary. By archiving asynchronously on a tick,
/// we don't need to weave a "tell the downloader you're done" callback
/// across that boundary. The trade-off is up to <see cref="ArchiveTickInterval"/>
/// of delay between Done and the row landing in history — acceptable
/// because nothing user-facing depends on instantaneous archive.</para>
///
/// <para><b>What gets passed as infohash:</b> only torrent jobs need
/// the V1/V2 hashes for their dedupe_key. The hashes aren't stored on
/// the <c>jobs</c> table directly (yet); they live in
/// <c>torrent_metadata</c>. For Phase 6 this service passes null for
/// both — the NZB key derivation works without them, and torrent keys
/// degrade to null (the dedupe column is nullable). Phase 7 wires
/// <c>torrent_metadata</c> lookup so torrent dedupe keys are also
/// populated.</para>
/// </summary>
public sealed class JobHistoryArchiveService(
    IJobRepository jobs,
    TimeProvider time,
    ILogger<JobHistoryArchiveService> logger)
    : BackgroundService
{
    /// <summary>How often to sweep the jobs table for terminal rows.</summary>
    public static readonly TimeSpan ArchiveTickInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ArchiveTickInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown — expected */ }
    }

    internal async Task SweepOnceAsync(CancellationToken ct)
    {
        try
        {
            var terminal = await jobs.ListByStateAsync(
                new[] { JobLifecycleState.Done, JobLifecycleState.Failed },
                limit: 256,
                ct).ConfigureAwait(false);
            if (terminal.Count == 0) return;

            foreach (var job in terminal)
            {
                try
                {
                    // Torrent infohash plumbing lands in Phase 7. For now
                    // pass null and let JobHistoryDedupeKey.Compute fall
                    // back to its protocol-default formula (which is
                    // exact for NZB and null-but-safe for torrent).
                    await jobs.ArchiveAsync(
                        job.Id,
                        torrentInfohashV1Hex: null,
                        torrentInfohashV2Hex: null,
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Failed to archive terminal job {JobId} ({State}).",
                        job.Id, job.State);
                }
            }
            logger.LogInformation(
                "Archived {Count} terminal job(s) to history.", terminal.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Archive sweep failed.");
        }
    }
}
