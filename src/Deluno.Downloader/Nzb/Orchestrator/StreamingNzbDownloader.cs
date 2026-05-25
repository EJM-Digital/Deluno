using System.Diagnostics;
using System.Threading.Channels;
using Deluno.Downloader.Nzb.MultiServer;
using Deluno.Downloader.Nzb.Parser;
using Deluno.Downloader.Nzb.Yenc;

namespace Deluno.Downloader.Nzb.Orchestrator;

/// <summary>
/// Production NZB downloader that streams article writes to disk
/// instead of buffering whole files in memory.
///
/// The spike's <c>NzbDownloader</c> buffered every article's decoded
/// payload until the file was complete, then flushed. A 100 MiB
/// download peaked at 1.15 GiB working set; a 12 GB Bluray remux would
/// have peaked around 120 GB. That blocked the spike from being
/// promoted.
///
/// This version, per the architecture doc:
/// <list type="number">
///   <item><description>Pre-allocates the output file with
///     <c>FileStream.SetLength(totalSize)</c> (sparse on NTFS / ext4 /
///     APFS).</description></item>
///   <item><description>Workers fetch articles in parallel via the
///     multi-server orchestrator.</description></item>
///   <item><description>Per-file write lock. On article decode: take
///     lock, <c>Seek(article.PartBegin - 1)</c>, <c>Write(payload)</c>,
///     release lock, null the byte[] reference. Worker is now eligible
///     for GC of the article bytes — memory becomes O(in-flight
///     articles × article size) ≈ tens of MB regardless of total file
///     size.</description></item>
/// </list>
/// </summary>
public sealed class StreamingNzbDownloader
{
    private readonly MultiServerArticleFetcher _fetcher;
    private readonly int _maxConcurrentArticles;

    public StreamingNzbDownloader(MultiServerArticleFetcher fetcher, int maxConcurrentArticles = 8)
    {
        _fetcher = fetcher;
        _maxConcurrentArticles = Math.Max(1, maxConcurrentArticles);
    }

    public async Task<NzbDownloadResult> DownloadAsync(
        NzbDocument document,
        string outputDir,
        IProgress<NzbDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);

        var sw = Stopwatch.StartNew();
        long totalBytesDeclared = document.TotalBytes;
        long totalBytesDownloaded = 0;
        var failedSegments = new List<FailedSegment>();
        var writtenFiles = new List<string>();

        foreach (var file in document.Files)
        {
            ct.ThrowIfCancellationRequested();
            var name = SanitizeFileName(file.FileName ?? $"unnamed-{file.GetHashCode():x}.bin");
            var path = Path.Combine(outputDir, name);

            // Pre-allocate the output to its full declared size so per-
            // article writes never grow it.
            await using var output = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 64 * 1024);
            if (file.TotalBytes > 0) output.SetLength(file.TotalBytes);

            var writeLock = new SemaphoreSlim(1, 1);
            var fileFailedCount = 0;
            var ch = Channel.CreateBounded<NzbSegment>(file.Segments.Count);
            foreach (var seg in file.Segments)
                await ch.Writer.WriteAsync(seg, ct).ConfigureAwait(false);
            ch.Writer.Complete();

            var workers = Enumerable.Range(0, _maxConcurrentArticles).Select(async _ =>
            {
                await foreach (var seg in ch.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    try
                    {
                        var rawBody = await _fetcher.FetchAsync(seg.MessageId, file.Date, ct).ConfigureAwait(false);
                        var article = YEncDecoder.Decode(rawBody);

                        // Position: yEnc PartBegin is 1-based, inclusive.
                        // Single-part articles (no =ypart) write from offset 0.
                        var offset = (article.PartBegin ?? 1) - 1;

                        await writeLock.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            output.Seek(offset, SeekOrigin.Begin);
                            await output.WriteAsync(article.Payload, ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            writeLock.Release();
                        }

                        var newTotal = Interlocked.Add(ref totalBytesDownloaded, article.Payload.LongLength);
                        progress?.Report(new NzbDownloadProgress(
                            CurrentFile: name,
                            BytesDownloaded: newTotal,
                            BytesTotal: totalBytesDeclared,
                            Elapsed: sw.Elapsed));

                        // Article bytes are now on disk — let the GC reclaim
                        // the byte[] as soon as the local scope exits.
                    }
                    catch (ArticleMissingOnAllServersException)
                    {
                        Interlocked.Increment(ref fileFailedCount);
                        lock (failedSegments)
                            failedSegments.Add(new FailedSegment(name, seg.Number, seg.MessageId, "missing on all servers"));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Interlocked.Increment(ref fileFailedCount);
                        lock (failedSegments)
                            failedSegments.Add(new FailedSegment(name, seg.Number, seg.MessageId, ex.Message));
                    }
                }
            });

            await Task.WhenAll(workers).ConfigureAwait(false);

            // Even if some articles failed, keep the partial file on disk
            // so par2 (Phase 4) can attempt repair.
            output.Flush();
            writtenFiles.Add(path);
        }

        return new NzbDownloadResult(
            OutputDirectory: outputDir,
            WrittenFiles: writtenFiles,
            FailedSegments: failedSegments,
            BytesDownloaded: totalBytesDownloaded,
            Elapsed: sw.Elapsed);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}

public sealed record NzbDownloadResult(
    string OutputDirectory,
    IReadOnlyList<string> WrittenFiles,
    IReadOnlyList<FailedSegment> FailedSegments,
    long BytesDownloaded,
    TimeSpan Elapsed);

public sealed record FailedSegment(string FileName, int SegmentNumber, string MessageId, string Reason);

public sealed record NzbDownloadProgress(
    string CurrentFile,
    long BytesDownloaded,
    long BytesTotal,
    TimeSpan Elapsed)
{
    public double Percent => BytesTotal == 0 ? 0 : (double)BytesDownloaded / BytesTotal * 100;
    public double MegabitsPerSecond => Elapsed.TotalSeconds <= 0 ? 0 : BytesDownloaded * 8.0 / 1_000_000 / Elapsed.TotalSeconds;
}
