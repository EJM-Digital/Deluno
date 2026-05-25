using Deluno.Downloader.Persistence;
using Deluno.Downloader.Torrent.Engine;
using Microsoft.Extensions.Logging;

namespace Deluno.Downloader.Engine;

/// <summary>
/// Drives one torrent job through the lifecycle via
/// <see cref="ITorrentEngine"/>. MonoTorrent does the protocol heavy
/// lifting; this executor adapts its event stream into our state
/// machine.
///
/// Current scope: .torrent file / .torrent bytes sources. Magnet
/// sources go through the future <c>MagnetIngestor</c> with its
/// leak-window guard (TBD).
/// </summary>
public sealed class TorrentJobExecutor(
    IJobRepository jobs,
    ITorrentEngine torrents,
    HttpClient httpClient,
    TimeProvider time,
    ILogger<TorrentJobExecutor> logger) : IDownloaderJobExecutor
{
    public DownloadProtocol Protocol => DownloadProtocol.Torrent;

    public async Task ExecuteAsync(JobRecord job, CancellationToken ct)
    {
        await jobs.TransitionAsync(job.Id, JobLifecycleState.Fetching, "Worker started", time.GetUtcNow(), ct);

        try
        {
            TorrentSource source = job.SourceKind switch
            {
                "magnet" => throw new NotSupportedException(
                    "Magnet sources require the leak-window-aware MagnetIngestor — not yet wired."),
                "torrent_file" => new TorrentSource.TorrentBytes(
                    await httpClient.GetByteArrayAsync(job.SourcePath, ct)),
                _ => throw new InvalidOperationException($"Unknown torrent source kind '{job.SourceKind}'."),
            };

            var downloadDir = Path.Combine(job.DownloadDir, job.Id);
            Directory.CreateDirectory(downloadDir);

            var addOptions = new TorrentAddOptions(
                Category: job.Category,
                DownloadDir: downloadDir,
                Priority: job.Priority);

            var handle = await torrents.AddAsync(source, addOptions, ct);
            logger.LogInformation(
                "Torrent {JobId} added: {InfohashV1} / total {TotalBytes} bytes / private={Private}",
                job.Id, handle.InfohashV1Hex, handle.TotalBytes, handle.IsPrivate);

            // Persist the resolved total size so telemetry has accurate
            // bytes-remaining math, and store the infohash as the output
            // dir hint (informational only).
            var current = await jobs.GetAsync(job.Id, ct);
            if (current is not null)
            {
                await jobs.UpsertAsync(current with
                {
                    TotalBytes = handle.TotalBytes,
                    OutputDir = downloadDir,
                    UpdatedAt = time.GetUtcNow(),
                }, ct);
            }

            // Subscribe to engine events and drive state machine. We exit
            // this method once the torrent reaches "Verified" (download
            // complete + hash-verified by MonoTorrent). Subsequent
            // extracting / post-processing / seeding decisions are
            // owned by the heartbeat-driven import pipeline, which will
            // also handle the Done → Seeding → Done transitions per
            // the architecture doc.
            await foreach (var ev in torrents.Events.WithCancellation(ct))
            {
                if (ev.JobId != handle.JobId) continue;

                switch (ev)
                {
                    case TorrentEngineEvent.StateChanged sc when sc.NewState.Contains("Seeding", StringComparison.OrdinalIgnoreCase):
                        // Reached 100% — MonoTorrent transitioned to Seeding.
                        await jobs.TransitionAsync(job.Id, JobLifecycleState.Reassembled,
                            "Torrent download complete (hash-verified by MonoTorrent).", time.GetUtcNow(), ct);
                        // Skip Verify (MonoTorrent already verified per-piece) →
                        // straight to Verified.
                        await jobs.TransitionAsync(job.Id, JobLifecycleState.Verified,
                            "Pieces hash-verified inline by MonoTorrent.", time.GetUtcNow(), ct);
                        // No extraction step for typical torrents (release group
                        // ships already-extracted MKV/MP4 in torrents); the
                        // heartbeat worker will skip extract if no archives.
                        await jobs.TransitionAsync(job.Id, JobLifecycleState.PostProcessed,
                            "Ready for import.", time.GetUtcNow(), ct);
                        return;

                    case TorrentEngineEvent.Failed f:
                        await jobs.TransitionAsync(job.Id, JobLifecycleState.Failed, f.Reason, time.GetUtcNow(), ct);
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Torrent job {JobId} failed.", job.Id);
            await jobs.TransitionAsync(job.Id, JobLifecycleState.Failed, ex.Message, time.GetUtcNow(), ct);
        }
    }
}
