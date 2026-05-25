using Deluno.Downloader.Torrent.Engine;

namespace Deluno.Downloader.Tests.Torrent;

/// <summary>
/// Lightweight tests of the MonoTorrent wrapper that don't need live
/// swarms. Network swarm + private-tracker compliance under real
/// announce traffic are Phase 7 work.
///
/// What we verify here:
/// <list type="bullet">
///   <item><description>Engine starts + stops cleanly (ClientEngine ctor
///     is the most likely place for version-bump incompatibilities
///     to surface).</description></item>
///   <item><description>V1 round-trip: real v1 .torrent created
///     in-memory via TorrentCreator, fed to AddAsync, infohash extracted
///     correctly (sha1, 40 hex chars).</description></item>
///   <item><description>V1+V2 hybrid (BEP-52) round-trip: both V1 (sha1)
///     and V2 (sha256) infohashes populate. This is the exact code path
///     the dedupe-key helper relies on for hybrid torrents.</description></item>
///   <item><description>Magnet ingestion with no peers/trackers
///     times out cleanly within MagnetMetadataTimeout rather than
///     wedging the worker. (The leak-window guard for private-suspect
///     magnets is tested separately in MagnetIngestorTests.)</description></item>
/// </list>
/// </summary>
public class MonoTorrentEngineTests
{
    [Fact]
    public async Task Engine_starts_and_stops_cleanly()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), $"deluno-mt-{Guid.NewGuid():N}");
        var dlDir = Path.Combine(cacheDir, "downloads");
        try
        {
            await using var engine = new MonoTorrentEngine(new MonoTorrentEngineOptions(
                CacheDir: cacheDir,
                DefaultDownloadDir: dlDir,
                ListenPort: 0,           // any free ephemeral port
                AllowUpnp: false,
                AllowLsd: false));

            await engine.StartAsync(CancellationToken.None);
            await engine.StopAsync(CancellationToken.None);
            // If we got here without throwing the version pin is compatible.
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                try { Directory.Delete(cacheDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task Round_trips_a_real_v1_torrent_through_AddAsync_and_extracts_infohash()
    {
        // V3 verification: construct an actual v1 .torrent in-memory via
        // MonoTorrent's own TorrentCreator, hand its bytes to our
        // wrapper's AddAsync, and assert that:
        //   - manager.InfoHashes.V1 surfaces as InfohashV1Hex
        //   - InfohashV2Hex is null (this is a v1-only torrent)
        //   - The wrapper doesn't crash on the MonoTorrent 3.x API
        //     surface we depend on (InfoHashes, V1OrV2, ToHex).
        var cacheDir = Path.Combine(Path.GetTempPath(), $"deluno-mt-{Guid.NewGuid():N}");
        var contentDir = Path.Combine(cacheDir, "src");
        var dlDir = Path.Combine(cacheDir, "dl");
        Directory.CreateDirectory(contentDir);
        await File.WriteAllBytesAsync(
            Path.Combine(contentDir, "payload.bin"),
            new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA, 0xBE });

        try
        {
            var creator = new MonoTorrent.TorrentCreator(MonoTorrent.TorrentType.V1Only);
            creator.Announces.Add(new List<string> { "http://tracker.example/announce" });
            var fileSource = new MonoTorrent.TorrentFileSource(contentDir);
            var dict = await creator.CreateAsync(fileSource, CancellationToken.None);
            var bytes = dict.Encode();

            await using var engine = new MonoTorrentEngine(new MonoTorrentEngineOptions(
                CacheDir: cacheDir,
                DefaultDownloadDir: dlDir,
                ListenPort: 0,
                AllowUpnp: false,
                AllowLsd: false));
            await engine.StartAsync(CancellationToken.None);

            var handle = await engine.AddAsync(
                new TorrentSource.TorrentBytes(bytes),
                new TorrentAddOptions(DownloadDir: dlDir),
                CancellationToken.None);

            Assert.NotEmpty(handle.InfohashV1Hex);
            Assert.Equal(40, handle.InfohashV1Hex.Length); // sha1 → 40 hex chars
            Assert.Null(handle.InfohashV2Hex);              // v1-only torrent has no v2 hash
            Assert.True(handle.TotalBytes > 0);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                try { Directory.Delete(cacheDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task Round_trips_a_hybrid_v1_v2_torrent_and_extracts_both_infohashes()
    {
        // V3 verification specifically for BEP-52 hybrid torrents: both
        // V1 and V2 InfoHash accessors must populate. This is the
        // exact code path the executor relies on to compute the
        // history.dedupe_key for v2/hybrid torrents.
        var cacheDir = Path.Combine(Path.GetTempPath(), $"deluno-mt-{Guid.NewGuid():N}");
        var contentDir = Path.Combine(cacheDir, "src");
        var dlDir = Path.Combine(cacheDir, "dl");
        Directory.CreateDirectory(contentDir);
        // V2 requires content >= one piece; use 32 KB (a single piece at
        // default piece size). Larger files would also work but are slow.
        var payload = new byte[32 * 1024];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);
        await File.WriteAllBytesAsync(Path.Combine(contentDir, "payload.bin"), payload);

        try
        {
            var creator = new MonoTorrent.TorrentCreator(MonoTorrent.TorrentType.V1V2Hybrid);
            creator.Announces.Add(new List<string> { "http://tracker.example/announce" });
            var fileSource = new MonoTorrent.TorrentFileSource(contentDir);
            var dict = await creator.CreateAsync(fileSource, CancellationToken.None);
            var bytes = dict.Encode();

            await using var engine = new MonoTorrentEngine(new MonoTorrentEngineOptions(
                CacheDir: cacheDir,
                DefaultDownloadDir: dlDir,
                ListenPort: 0,
                AllowUpnp: false,
                AllowLsd: false));
            await engine.StartAsync(CancellationToken.None);

            var handle = await engine.AddAsync(
                new TorrentSource.TorrentBytes(bytes),
                new TorrentAddOptions(DownloadDir: dlDir),
                CancellationToken.None);

            Assert.NotEmpty(handle.InfohashV1Hex);
            Assert.Equal(40, handle.InfohashV1Hex.Length);   // v1 sha1
            Assert.NotNull(handle.InfohashV2Hex);
            Assert.Equal(64, handle.InfohashV2Hex!.Length);  // v2 sha256
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                try { Directory.Delete(cacheDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task Magnet_with_no_peers_times_out_within_configured_window()
    {
        // Magnet ingestion is now wired. With no trackers and no real
        // swarm, BEP-9 metadata fetch will never complete — we verify
        // the engine raises TimeoutException after MagnetMetadataTimeout
        // instead of hanging forever. The orchestrator's leak-window
        // guard (MagnetIngestor.GuardOrThrow) is tested separately —
        // here we only confirm that "magnet that resolves nothing"
        // doesn't wedge the worker.
        var cacheDir = Path.Combine(Path.GetTempPath(), $"deluno-mt-{Guid.NewGuid():N}");
        try
        {
            await using var engine = new MonoTorrentEngine(new MonoTorrentEngineOptions(
                CacheDir: cacheDir, ListenPort: 0, AllowUpnp: false, AllowLsd: false)
                {
                    // 2-second timeout keeps the test fast.
                    MagnetMetadataTimeout = TimeSpan.FromSeconds(2),
                });
            await engine.StartAsync(CancellationToken.None);

            var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
                engine.AddAsync(
                    // Random infohash + no trackers — there's literally
                    // nothing to fetch metadata from.
                    new TorrentSource.Magnet("magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a"),
                    new TorrentAddOptions(),
                    CancellationToken.None));
            Assert.Contains("Magnet metadata fetch timed out", ex.Message);
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                try { Directory.Delete(cacheDir, recursive: true); } catch { }
            }
        }
    }
}
