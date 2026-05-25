using Deluno.Downloader.Torrent.Engine;

namespace Deluno.Downloader.Tests.Torrent;

/// <summary>
/// Lightweight tests of the MonoTorrent wrapper that don't need live
/// swarms. Network swarm + .torrent ingestion + private-tracker
/// compliance under real announce traffic are Phase 7 work.
///
/// What we verify here:
/// <list type="bullet">
///   <item><description>Engine can start + stop cleanly (the ClientEngine
///     constructor is the most likely place for version-bump
///     incompatibilities; this catches that early).</description></item>
///   <item><description>Adding a magnet source raises a friendly
///     NotSupportedException pointing at the orchestrator
///     MagnetIngestor (the leak-window guard).</description></item>
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
    public async Task Magnet_source_throws_pending_orchestrator_ingestor_wiring()
    {
        // Phase 3b explicitly defers magnet ingestion to the orchestrator
        // (which owns the leak-window guard for private-suspect categories).
        // The engine itself rejects magnet sources with a clear message.
        var cacheDir = Path.Combine(Path.GetTempPath(), $"deluno-mt-{Guid.NewGuid():N}");
        try
        {
            await using var engine = new MonoTorrentEngine(new MonoTorrentEngineOptions(
                CacheDir: cacheDir, ListenPort: 0, AllowUpnp: false, AllowLsd: false));
            await engine.StartAsync(CancellationToken.None);

            var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
                engine.AddAsync(
                    new TorrentSource.Magnet("magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a"),
                    new TorrentAddOptions(),
                    CancellationToken.None));
            Assert.Contains("MagnetIngestor", ex.Message);
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
