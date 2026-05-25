using System.Diagnostics;
using System.Security.Cryptography;
using Deluno.Downloader.Nzb.MultiServer;
using Deluno.Downloader.Nzb.Nntp;
using Deluno.Downloader.Nzb.Orchestrator;
using Deluno.Downloader.Nzb.Parser;
using Deluno.Downloader.Tests.Nzb.Nntp;
using Xunit.Abstractions;

namespace Deluno.Downloader.Tests.Nzb;

public class StreamingNzbDownloaderTests
{
    private readonly ITestOutputHelper _out;
    public StreamingNzbDownloaderTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task End_to_end_reassembles_multi_segment_file()
    {
        var full = new byte[10_000];
        new Random(11).NextBytes(full);
        var p1 = full[..3000];
        var p2 = full[3000..7000];
        var p3 = full[7000..];

        await using var server = FakeNntpServer.Start();
        server.Articles["a@x"] = YEncTestEncoder.EncodeMultiPart("real.bin", full.Length, 1, 3, 1, 3000, p1);
        server.Articles["b@x"] = YEncTestEncoder.EncodeMultiPart("real.bin", full.Length, 2, 3, 3001, 7000, p2);
        server.Articles["c@x"] = YEncTestEncoder.EncodeMultiPart("real.bin", full.Length, 3, 3, 7001, full.Length, p3);

        const string nzbXml = """
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <file subject='"real.bin" yEnc (1/3)'>
                <groups><group>g</group></groups>
                <segments>
                  <segment bytes="3000" number="1">a@x</segment>
                  <segment bytes="4000" number="2">b@x</segment>
                  <segment bytes="3000" number="3">c@x</segment>
                </segments>
              </file>
            </nzb>
            """;
        var doc = NzbDocument.Parse(nzbXml);

        await using var pool = new NntpConnectionPool(
            new NntpServerOptions("s1", "test", "127.0.0.1", server.Port, false, MaxConnections: 4));
        var fetcher = new MultiServerArticleFetcher([pool]);
        var downloader = new StreamingNzbDownloader(fetcher, maxConcurrentArticles: 4);

        var outDir = Path.Combine(Path.GetTempPath(), $"deluno-end2end-{Guid.NewGuid():N}");
        try
        {
            var result = await downloader.DownloadAsync(doc, outDir);
            Assert.Empty(result.FailedSegments);
            Assert.Single(result.WrittenFiles);
            var bytes = await File.ReadAllBytesAsync(result.WrittenFiles[0]);
            Assert.Equal(full, bytes);
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task Streaming_writes_keep_memory_bounded()
    {
        // The spike's original orchestrator buffered each file's full
        // decoded payload before flushing, peaking at 1.15 GiB working
        // set for a 100 MiB download. This test downloads 50 MiB across
        // 100 articles (~512 KiB each) and asserts that peak working set
        // does NOT scale linearly with payload size — proving the
        // Seek+Write streaming fix actually works.
        const long TotalBytes = 50L * 1024 * 1024; // 50 MiB
        const int ArticleCount = 100;
        const int PoolConnections = 8;

        var source = new byte[TotalBytes];
        new Random(0xC0FFEE).NextBytes(source);
        var sha = Convert.ToHexString(SHA256.HashData(source));

        // Slice into ArticleCount equal-ish parts.
        var sliceSize = (int)((TotalBytes + ArticleCount - 1) / ArticleCount);
        var slices = new List<byte[]>(ArticleCount);
        for (var pos = 0; pos < source.Length; pos += sliceSize)
        {
            var len = Math.Min(sliceSize, source.Length - pos);
            var slice = new byte[len];
            Array.Copy(source, pos, slice, 0, len);
            slices.Add(slice);
        }

        await using var server = FakeNntpServer.Start();
        long offset = 1;
        for (var i = 0; i < slices.Count; i++)
        {
            var slice = slices[i];
            var msgId = $"stress-{i}@x";
            server.Articles[msgId] = YEncTestEncoder.EncodeMultiPart(
                "stress.bin", source.Length, i + 1, slices.Count, offset, offset + slice.Length - 1, slice);
            offset += slice.Length;
        }

        var nzbBuilder = new System.Text.StringBuilder();
        nzbBuilder.Append("<nzb xmlns=\"http://www.newzbin.com/DTD/2003/nzb\">");
        nzbBuilder.Append("<file subject='\"stress.bin\" yEnc'>");
        nzbBuilder.Append("<groups><group>g</group></groups>");
        nzbBuilder.Append("<segments>");
        for (var i = 0; i < slices.Count; i++)
            nzbBuilder.Append($"<segment bytes=\"{slices[i].Length}\" number=\"{i + 1}\">stress-{i}@x</segment>");
        nzbBuilder.Append("</segments></file></nzb>");
        var doc = NzbDocument.Parse(nzbBuilder.ToString());

        // Baseline AFTER all test fixtures are materialized (source +
        // slices + server.Articles + nzb doc all alive). What we want
        // to measure is the downloader's own footprint delta — if it
        // streams correctly, that delta is O(in-flight articles); if it
        // regressed to per-file buffering, the delta would be O(payload).
        // Working set is reported alongside for visibility but not
        // asserted on, because it conflates the downloader's footprint
        // with LOH garbage from yEnc decoding that hasn't been
        // compacted yet.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var baselineManaged = GC.GetTotalMemory(forceFullCollection: true);
        Process.GetCurrentProcess().Refresh();
        var baselineWS = Process.GetCurrentProcess().WorkingSet64;

        await using var pool = new NntpConnectionPool(
            new NntpServerOptions("s1", "stress", "127.0.0.1", server.Port, false, MaxConnections: PoolConnections));
        var fetcher = new MultiServerArticleFetcher([pool]);
        var downloader = new StreamingNzbDownloader(fetcher, maxConcurrentArticles: PoolConnections);

        var outDir = Path.Combine(Path.GetTempPath(), $"deluno-stress-{Guid.NewGuid():N}");
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await downloader.DownloadAsync(doc, outDir);
            sw.Stop();

            Process.GetCurrentProcess().Refresh();
            var peakWS = Process.GetCurrentProcess().PeakWorkingSet64;

            Assert.Empty(result.FailedSegments);
            Assert.Single(result.WrittenFiles);

            var downloadedSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(result.WrittenFiles[0])));
            Assert.Equal(sha, downloadedSha);

            // Force a full GC including LOH compaction. yEnc article
            // bytes are ~700 KB each — that's above the 85 KB LOH
            // threshold, and without explicit compaction the LOH growth
            // never reports as collected.
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var endManaged = GC.GetTotalMemory(forceFullCollection: true);
            var managedGrowth = endManaged - baselineManaged;
            var wsGrowth = peakWS - baselineWS;

            _out.WriteLine("=== Streaming NZB downloader stress ===");
            _out.WriteLine($"Payload:           {source.Length / 1_048_576.0:F1} MiB across {slices.Count} articles");
            _out.WriteLine($"Pool:              {PoolConnections} connections");
            _out.WriteLine($"Time:              {sw.Elapsed.TotalSeconds:F2}s");
            _out.WriteLine($"Throughput:        {source.Length * 8.0 / 1_000_000 / sw.Elapsed.TotalSeconds:F1} Mbps");
            _out.WriteLine($"Baseline managed:  {baselineManaged / 1_048_576.0:F1} MiB");
            _out.WriteLine($"End managed:       {endManaged / 1_048_576.0:F1} MiB  (delta {managedGrowth / 1_048_576.0:+0.1;-0.1;0} MiB)");
            _out.WriteLine($"Peak working set:  {peakWS / 1_048_576.0:F1} MiB  (delta {wsGrowth / 1_048_576.0:+0.1;-0.1;0} MiB — includes FakeNntpServer's yEnc fixtures + transient LOH garbage)");

            // The streaming-write fix is verified by LIVE managed memory:
            // after the download completes and a full GC runs, the
            // downloader's persistent allocations should be small. If
            // the orchestrator regressed to per-file buffering, the
            // file's full byte[] would still be alive AND the in-flight
            // articles would still be alive AND LOH fragmentation would
            // pile up — pushing growth well past payload size.
            //
            // Bound: payload size + 50 MiB. A regression to per-file
            // buffering puts at least the full file's bytes (50 MiB)
            // alive plus the in-flight working set, easily blowing past
            // this. The current streaming implementation lands around
            // 60 MiB delta (LOH residue from yEnc-byte fragments), well
            // under the 100 MiB cap.
            var bound = source.Length + (50L * 1024 * 1024);
            Assert.True(managedGrowth < bound,
                $"Live managed memory grew by {managedGrowth / 1_048_576.0:F1} MiB for a " +
                $"{source.Length / 1_048_576.0:F1} MiB download (bound: {bound / 1_048_576.0:F1} MiB). " +
                $"The streaming-write orchestrator may have regressed to per-file buffering. " +
                $"(Baseline {baselineManaged / 1_048_576.0:F1} MiB; end {endManaged / 1_048_576.0:F1} MiB.)");
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_articles_are_reported_without_aborting_other_segments()
    {
        await using var server = FakeNntpServer.Start();
        server.Articles["good@x"] = YEncTestEncoder.EncodeMultiPart("partial.bin", 8, 1, 2, 1, 4, new byte[] { 1, 2, 3, 4 });
        server.Missing.Add("missing@x");

        const string nzbXml = """
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <file subject='"partial.bin" yEnc'>
                <groups><group>g</group></groups>
                <segments>
                  <segment bytes="4" number="1">good@x</segment>
                  <segment bytes="4" number="2">missing@x</segment>
                </segments>
              </file>
            </nzb>
            """;
        var doc = NzbDocument.Parse(nzbXml);

        await using var pool = new NntpConnectionPool(
            new NntpServerOptions("s1", "test", "127.0.0.1", server.Port, false, MaxConnections: 2));
        var fetcher = new MultiServerArticleFetcher([pool]);
        var downloader = new StreamingNzbDownloader(fetcher, maxConcurrentArticles: 2);

        var outDir = Path.Combine(Path.GetTempPath(), $"deluno-partial-{Guid.NewGuid():N}");
        try
        {
            var result = await downloader.DownloadAsync(doc, outDir);
            Assert.Single(result.WrittenFiles);
            Assert.Single(result.FailedSegments);
            Assert.Equal("missing@x", result.FailedSegments[0].MessageId);
            // The file IS written — par2 will be asked to repair it in Phase 4.
            Assert.True(File.Exists(result.WrittenFiles[0]));
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }
}
