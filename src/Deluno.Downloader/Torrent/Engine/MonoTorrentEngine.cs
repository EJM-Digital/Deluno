using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Deluno.Downloader.Torrent.Magnet;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrentSource = MonoTorrent.Client.TorrentManager;

namespace Deluno.Downloader.Torrent.Engine;

/// <summary>
/// MonoTorrent-backed implementation of <see cref="ITorrentEngine"/>.
///
/// Phase 3b scope: the wrapper API, magnet/.torrent ingestion, infohash
/// extraction, private-tracker policy enforcement at TorrentSettings
/// build time, lifecycle events emitted into an in-process channel.
///
/// What this implementation does NOT do yet (Phase 3b polish + Phase 7):
/// <list type="bullet">
///   <item><description>Fast-resume blob persistence — MonoTorrent gives us a
///     <c>byte[]</c> snapshot per torrent; we need to round-trip through
///     <c>torrent_metadata.fast_resume_blob</c>. Wiring lands when we
///     integrate with the orchestrator state machine.</description></item>
///   <item><description>Magnet leak-window mitigation — see
///     <see cref="MagnetIngestor"/> (TBD when the orchestrator starts
///     using this engine for real).</description></item>
///   <item><description>Ratio/time seeding targets — MonoTorrent exposes
///     ratio events; we hook them in when the orchestrator owns the
///     stop-seeding decision.</description></item>
/// </list>
///
/// Live-swarm integration tests are Phase 7 work (require
/// internet-connected swarms). What's verified here in CI: API surface,
/// magnet parsing, infohash computation, private-tracker policy data.
/// </summary>
public sealed class MonoTorrentEngine : ITorrentEngine
{
    private ClientEngine? _engine;
    private readonly Dictionary<string, MonoTorrentSource> _managers = new(StringComparer.Ordinal);
    private readonly Channel<TorrentEngineEvent> _events = Channel.CreateUnbounded<TorrentEngineEvent>(
        new UnboundedChannelOptions { SingleReader = false });
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>Default global settings; overridden per-torrent for private torrents.</summary>
    public MonoTorrentEngineOptions DefaultOptions { get; }

    public MonoTorrentEngine(MonoTorrentEngineOptions? options = null)
        => DefaultOptions = options ?? new MonoTorrentEngineOptions();

    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_engine is not null) return;

        var builder = new EngineSettingsBuilder
        {
            CacheDirectory = DefaultOptions.CacheDir,
            ListenEndPoints = new Dictionary<string, System.Net.IPEndPoint>
            {
                ["ipv4"] = new(System.Net.IPAddress.Any, DefaultOptions.ListenPort),
                ["ipv6"] = new(System.Net.IPAddress.IPv6Any, DefaultOptions.ListenPort),
            },
            // Global defaults — per-torrent overrides for private torrents
            // applied at AddAsync time via PrivateTrackerPolicy.
            AllowPortForwarding = DefaultOptions.AllowUpnp,
            AllowLocalPeerDiscovery = DefaultOptions.AllowLsd,
            MaximumConnections = DefaultOptions.MaxGlobalConnections,
            MaximumUploadRate = DefaultOptions.MaxUploadBytesPerSecond,
            MaximumDownloadRate = DefaultOptions.MaxDownloadBytesPerSecond,
        };

        _engine = new ClientEngine(builder.ToSettings());
        await Task.CompletedTask; // ClientEngine construction is synchronous in MonoTorrent 3.x
    }

    public async Task<TorrentJobHandle> AddAsync(
        TorrentSource source, TorrentAddOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_engine is null) throw new InvalidOperationException("Call StartAsync first.");

        var torrent = await LoadTorrentAsync(source, ct).ConfigureAwait(false);
        var downloadDir = options.DownloadDir ?? DefaultOptions.DefaultDownloadDir;
        Directory.CreateDirectory(downloadDir);

        var settingsBuilder = new TorrentSettingsBuilder();
        ApplyPolicyIfPrivate(settingsBuilder, torrent, options);

        var manager = await _engine.AddAsync(torrent, downloadDir, settingsBuilder.ToSettings()).ConfigureAwait(false);

        var jobId = manager.InfoHashes.V1OrV2.ToHex();
        lock (_gate) _managers[jobId] = manager;

        // Surface "added" + future lifecycle events through the channel.
        WireManagerEvents(jobId, manager);

        await _events.Writer.WriteAsync(new TorrentEngineEvent.Added(
            jobId, DateTimeOffset.UtcNow, torrent.Name), ct).ConfigureAwait(false);

        var infohashV1 = manager.InfoHashes.V1?.ToHex();
        var infohashV2 = manager.InfoHashes.V2?.ToHex();
        return new TorrentJobHandle(
            JobId: jobId,
            DisplayName: torrent.Name,
            InfohashV1Hex: infohashV1 ?? string.Empty,
            InfohashV2Hex: infohashV2,
            TotalBytes: torrent.Size,
            IsPrivate: torrent.IsPrivate);
    }

    public Task StopAsync(CancellationToken ct)
    {
        if (_engine is null) return Task.CompletedTask;
        return _engine.StopAllAsync();
    }

    public async Task PauseAsync(string jobId, CancellationToken ct)
    {
        var mgr = GetManager(jobId);
        if (mgr is null) return;
        await mgr.PauseAsync().ConfigureAwait(false);
    }

    public async Task ResumeAsync(string jobId, CancellationToken ct)
    {
        var mgr = GetManager(jobId);
        if (mgr is null) return;
        await mgr.StartAsync().ConfigureAwait(false);
    }

    public async Task RemoveAsync(string jobId, bool deleteData, CancellationToken ct)
    {
        var mgr = GetManager(jobId);
        if (mgr is null || _engine is null) return;
        await _engine.RemoveAsync(mgr,
            deleteData ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly).ConfigureAwait(false);
        lock (_gate) _managers.Remove(jobId);
    }

    public async Task ForceRecheckAsync(string jobId, CancellationToken ct)
    {
        var mgr = GetManager(jobId);
        if (mgr is null) return;
        await mgr.HashCheckAsync(autoStart: true).ConfigureAwait(false);
    }

    public IAsyncEnumerable<TorrentEngineEvent> Events => ReadEventsAsync();

    private async IAsyncEnumerable<TorrentEngineEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var ev in _events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return ev;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _events.Writer.TryComplete();
        if (_engine is not null)
        {
            try { await _engine.StopAllAsync().ConfigureAwait(false); } catch { }
            _engine.Dispose();
        }
    }

    private MonoTorrentSource? GetManager(string jobId)
    {
        lock (_gate)
        {
            _managers.TryGetValue(jobId, out var mgr);
            return mgr;
        }
    }

    private async Task<MonoTorrent.Torrent> LoadTorrentAsync(TorrentSource source, CancellationToken ct)
    {
        switch (source)
        {
            case TorrentSource.TorrentFile file:
                return await MonoTorrent.Torrent.LoadAsync(file.Path).ConfigureAwait(false);
            case TorrentSource.TorrentBytes bytes:
                return await MonoTorrent.Torrent.LoadAsync(bytes.Content).ConfigureAwait(false);
            case TorrentSource.Magnet magnet:
                // For magnet sources MonoTorrent's AddAsync(MagnetLink) is
                // the proper path — it negotiates metadata via BEP-9
                // before yielding a TorrentManager. This branch routes via
                // a different overload of AddAsync but we hide that
                // behind the same Add → JobHandle contract. The leak-
                // window guard belongs in MagnetIngestor and is wired
                // when the orchestrator owns torrent add.
                _ = MagnetUriParser.Parse(magnet.MagnetUri); // validate
                throw new NotSupportedException(
                    "Magnet ingestion via the engine is not wired in Phase 3b — " +
                    "the orchestrator's MagnetIngestor (with leak-window guard) handles it.");
            default:
                throw new ArgumentOutOfRangeException(nameof(source));
        }
    }

    /// <summary>
    /// Applies <see cref="PrivateTrackerPolicy.Required"/> overrides to
    /// a per-torrent settings builder when the torrent is private.
    /// This is the SINGLE PATH any add operation goes through; a
    /// private torrent cannot bypass this even if the user has the
    /// global DHT/PEX/LSD flags on.
    /// </summary>
    private static void ApplyPolicyIfPrivate(
        TorrentSettingsBuilder settingsBuilder,
        MonoTorrent.Torrent torrent,
        TorrentAddOptions options)
    {
        var isPrivate = options.IsPrivateOverride ?? torrent.IsPrivate;
        if (!isPrivate) return;

        var policy = PrivateTrackerPolicy.Required;
        settingsBuilder.AllowDht = policy.DhtEnabled;
        settingsBuilder.AllowPeerExchange = policy.PexEnabled;
        // LSD is a global engine setting; per-torrent suppression is
        // enforced by the engine when settingsBuilder.AllowDht is false
        // for private torrents (MonoTorrent links them).
    }

    private void WireManagerEvents(string jobId, MonoTorrentSource manager)
    {
        manager.TorrentStateChanged += (_, args) =>
        {
            _events.Writer.TryWrite(new TorrentEngineEvent.StateChanged(
                jobId, DateTimeOffset.UtcNow, args.NewState.ToString()));
        };
        // Progress is polled by the orchestrator; we don't emit
        // per-piece events here (would flood the channel for large
        // torrents). The orchestrator reads manager.Progress on a
        // SignalR throttle.
    }
}

/// <summary>
/// Engine-global defaults. Per-torrent overrides come from
/// <see cref="TorrentAddOptions"/> + <see cref="PrivateTrackerPolicy"/>.
/// </summary>
public sealed record MonoTorrentEngineOptions(
    string CacheDir = "downloader-cache",
    string DefaultDownloadDir = "downloads",
    int ListenPort = 51413,
    bool AllowUpnp = true,
    bool AllowLsd = true,
    int MaxGlobalConnections = 200,
    int MaxUploadBytesPerSecond = 0,        // 0 = unlimited
    int MaxDownloadBytesPerSecond = 0);
