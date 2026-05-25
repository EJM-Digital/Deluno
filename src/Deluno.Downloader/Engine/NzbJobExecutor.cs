using Deluno.Downloader.Extraction;
using Deluno.Downloader.Nzb.MultiServer;
using Deluno.Downloader.Nzb.Nntp;
using Deluno.Downloader.Nzb.Par2;
using Deluno.Downloader.Nzb.Orchestrator;
using Deluno.Downloader.Nzb.Parser;
using Deluno.Downloader.Persistence;
using Deluno.Downloader.Postprocessing;
using Microsoft.Extensions.Logging;

namespace Deluno.Downloader.Engine;

/// <summary>
/// Drives one NZB job end-to-end:
/// <code>
///   Queued → Fetching → Reassembled → Verify → (Verified | Repair → Verified)
///          → Extracting? → Extracted? → PostProcessed
/// </code>
///
/// The <c>ImportPending → Done</c> tail is handled externally by
/// Deluno's existing telemetry-polling + ImportPipelineService chain —
/// once we hit PostProcessed and set the job's OutputDir, the
/// heartbeat worker's next snapshot tick picks it up via the
/// BuiltinNzbAdapter.
/// </summary>
public sealed class NzbJobExecutor(
    IJobRepository jobs,
    INzbServerRepository nzbServers,
    HttpClient httpClient,
    ArchiveExtractionPipeline extraction,
    PostProcessingPipeline postProcessing,
    IPar2Service par2,
    TimeProvider time,
    ILogger<NzbJobExecutor> logger) : IDownloaderJobExecutor
{
    public DownloadProtocol Protocol => DownloadProtocol.Nzb;

    public async Task ExecuteAsync(JobRecord job, CancellationToken ct)
    {
        await jobs.TransitionAsync(job.Id, JobLifecycleState.Fetching, "Worker started", time.GetUtcNow(), ct);

        var servers = await nzbServers.ListEnabledAsync(ct);
        if (servers.Count == 0)
        {
            await jobs.TransitionAsync(job.Id, JobLifecycleState.Failed,
                "No enabled NZB servers configured.", time.GetUtcNow(), ct);
            return;
        }

        var pools = servers.Select(s => new NntpConnectionPool(s)).ToList();
        try
        {
            var nzbBytes = await FetchNzbAsync(job.SourcePath, ct);
            var doc = NzbDocument.Parse(System.Text.Encoding.UTF8.GetString(nzbBytes));

            var downloadDir = Path.Combine(job.DownloadDir, job.Id);
            Directory.CreateDirectory(downloadDir);

            var fetcher = new MultiServerArticleFetcher(pools);
            var downloader = new StreamingNzbDownloader(
                fetcher,
                maxConcurrentArticles: servers.Sum(s => s.MaxConnections));

            var result = await downloader.DownloadAsync(doc, downloadDir, progress: null, ct);
            await jobs.TransitionAsync(job.Id, JobLifecycleState.Reassembled,
                $"Downloaded {result.BytesDownloaded:N0} bytes ({result.FailedSegments.Count} failed segments).",
                time.GetUtcNow(), ct);

            // par2 verify (if par2 files in the NZB).
            var par2Files = result.WrittenFiles.Where(f =>
                f.EndsWith(".par2", StringComparison.OrdinalIgnoreCase)).ToList();
            if (par2Files.Count > 0)
            {
                await jobs.TransitionAsync(job.Id, JobLifecycleState.Verify, "par2 verify", time.GetUtcNow(), ct);
                var par2Main = SelectMainPar2File(par2Files);
                var verify = await par2.VerifyAsync(par2Main, progress: null, ct);

                if (verify.Outcome == Par2Outcome.NeedsRepair)
                {
                    await jobs.TransitionAsync(job.Id, JobLifecycleState.Repair, "par2 repair", time.GetUtcNow(), ct);
                    var repair = await par2.RepairAsync(par2Main, progress: null, ct);
                    if (!repair.Repaired)
                    {
                        await jobs.TransitionAsync(job.Id, JobLifecycleState.Failed,
                            $"par2 repair failed: {repair.Message}", time.GetUtcNow(), ct);
                        return;
                    }
                    await jobs.TransitionAsync(job.Id, JobLifecycleState.Verified, "par2 repaired", time.GetUtcNow(), ct);
                }
                else if (verify.Outcome == Par2Outcome.Ok)
                {
                    await jobs.TransitionAsync(job.Id, JobLifecycleState.Verified, "par2 ok", time.GetUtcNow(), ct);
                }
                else
                {
                    await jobs.TransitionAsync(job.Id, JobLifecycleState.Failed,
                        $"par2 verify failed: {verify.Outcome}. {verify.Message}", time.GetUtcNow(), ct);
                    return;
                }
            }

            // Extract any archives present in the result set.
            var archives = result.WrittenFiles
                .Select(f => (Path: f, Format: ArchiveFormatDetector.DetectByExtension(f)))
                .Where(t => t.Format != ArchiveFormat.Unknown)
                .ToList();

            if (archives.Count > 0)
            {
                await jobs.TransitionAsync(job.Id, JobLifecycleState.Extracting, "extract", time.GetUtcNow(), ct);
                foreach (var (path, _) in archives)
                {
                    var extractDir = Path.Combine(downloadDir, "_extracted");
                    var extractResult = await extraction.ExtractAsync(path, extractDir, password: null, progress: null, ct);
                    if (!extractResult.Succeeded)
                    {
                        await jobs.TransitionAsync(job.Id, JobLifecycleState.Failed,
                            $"Extraction failed for {Path.GetFileName(path)}: {extractResult.FailureReason}",
                            time.GetUtcNow(), ct);
                        return;
                    }
                }
                await jobs.TransitionAsync(job.Id, JobLifecycleState.Extracted, "extract ok", time.GetUtcNow(), ct);
            }

            // Post-processing (sample filter / flatten / sanitize) over the
            // final output set. If we extracted, that's the _extracted/
            // dir; otherwise it's the raw downloaded files (e.g. a bare
            // .mkv NZB).
            var ppWorkingDir = archives.Count > 0
                ? Path.Combine(downloadDir, "_extracted")
                : downloadDir;
            var ppInputFiles = Directory.Exists(ppWorkingDir)
                ? Directory.EnumerateFiles(ppWorkingDir, "*", SearchOption.AllDirectories).ToList()
                : result.WrittenFiles.Where(f => !f.EndsWith(".par2", StringComparison.OrdinalIgnoreCase)).ToList();

            var finalFiles = await postProcessing.RunAsync(ppWorkingDir, ppInputFiles, ct);
            await UpdateOutputDirAsync(job, ppWorkingDir, ct);
            await jobs.TransitionAsync(job.Id, JobLifecycleState.PostProcessed,
                $"{finalFiles.Count} files ready for import.", time.GetUtcNow(), ct);

            // Telemetry-polling chain takes over from here: the
            // BuiltinNzbAdapter snapshot will show status=importReady
            // with the OutputDir as SourcePath; the heartbeat worker
            // enqueues filesystem.import.execute; ImportPipelineService
            // moves the file into the library and raises
            // RecordImportOutcomeAsync; that flips us to ImportPending
            // and then Done via the existing pipeline.
            logger.LogInformation("NZB job {JobId} reached PostProcessed; import pipeline takeover.", job.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "NZB job {JobId} failed.", job.Id);
            await jobs.TransitionAsync(job.Id, JobLifecycleState.Failed, ex.Message, time.GetUtcNow(), ct);
        }
        finally
        {
            foreach (var pool in pools) await pool.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Picks the par2 file to invoke par2cmdline against — preferring
    /// the index volume (smallest .par2 in the set, conventionally
    /// named <c>basename.par2</c>) over the recovery volumes
    /// (<c>basename.vol000+01.par2</c> etc.). par2cmdline walks the
    /// rest of the set itself from the index.
    /// </summary>
    private static string SelectMainPar2File(IReadOnlyList<string> par2Files)
    {
        // Smallest = the index volume (no recovery blocks).
        return par2Files.OrderBy(f => new FileInfo(f).Length).First();
    }

    private async Task UpdateOutputDirAsync(JobRecord job, string outputDir, CancellationToken ct)
    {
        var current = await jobs.GetAsync(job.Id, ct);
        if (current is null) return;
        await jobs.UpsertAsync(current with { OutputDir = outputDir, UpdatedAt = time.GetUtcNow() }, ct);
    }

    private async Task<byte[]> FetchNzbAsync(string url, CancellationToken ct)
    {
        // Many indexers protect downloads with an API key in the query
        // string; the dispatched URL already includes it. Direct HTTP
        // fetch is sufficient.
        using var resp = await httpClient.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }
}
