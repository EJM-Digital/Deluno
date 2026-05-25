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
            // Fast-resume persistence: MonoTorrent transparently snapshots
            // per-torrent piece-completion + hash state into CacheDirectory
            // on shutdown and reloads it on AddAsync. Means torrents don't
            // re-hash from zero across Deluno restarts — gigabyte savings
            // for users with large libraries. (BEP-0046 style; the cache
            // file format is internal to MonoTorrent.)
            AutoSaveLoadFastResume = true,
        };

        _engine = new ClientEngine(builder.ToSettings());
        await Task.CompletedTask; // ClientEngine construction is synchronous in MonoTorrent 3.x
    }

    public async Task<TorrentJobHandle> AddAsync(
        TorrentSource source, TorrentAddOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_engine is null) throw new InvalidOperationException("Call StartAsync first.");

        var downloadDir = options.DownloadDir ?? DefaultOptions.DefaultDownloadDir;
        Directory.CreateDirectory(downloadDir);

        return source switch
        {
            TorrentSource.Magnet m => await AddMagnetAsync(m, options, downloadDir, ct).ConfigureAwait(false),
            TorrentSource.TorrentFile f => await AddTorrentAsync(
                await MonoTorrent.Torrent.LoadAsync(f.Path).ConfigureAwait(false),
                options, downloadDir, ct).ConfigureAwait(false),
            TorrentSource.TorrentBytes b => await AddTorrentAsync(
                await MonoTorrent.Torrent.LoadAsync(b.Content).ConfigureAwait(false),
                options, downloadDir, ct).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
    }

    /// <summary>
    /// Add a fully-loaded .torrent. Settings are derived from the
    /// torrent's IsPrivate flag and any caller override.
    /// </summary>
    private async Task<TorrentJobHandle> AddTorrentAsync(
        MonoTorrent.Torrent torrent, TorrentAddOptions options, string downloadDir, CancellationToken ct)
    {
        var settingsBuilder = new TorrentSettingsBuilder();
        ApplyPolicyIfPrivate(settingsBuilder, torrent.IsPrivate, options);

        var manager = await _engine!.AddAsync(torrent, downloadDir, settingsBuilder.ToSettings()).ConfigureAwait(false);
        return RegisterAndBuildHandle(manager, torrent.Name, torrent.Size, torrent.IsPrivate, ct);
    }

    /// <summary>
    /// Add a magnet link. The orchestrator (TorrentJobExecutor) has
    /// already called <see cref="MagnetIngestor.GuardOrThrow"/> by the
    /// time we get here — that's the leak-window check. What we do
    /// HERE is consult the same decision to pick metadata-fetch
    /// settings: TrackerOnly disables DHT + PEX so the infohash never
    /// hits a public network during the metadata-fetch window for
    /// private-suspect destinations.
    /// </summary>
    private async Task<TorrentJobHandle> AddMagnetAsync(
        TorrentSource.Magnet source, TorrentAddOptions options, string downloadDir, CancellationToken ct)
    {
        var parsed = MagnetUriParser.Parse(source.MagnetUri);
        var hint = new MagnetIngestionHint(
            IsPrivateSuspect: options.IsPrivateOverride,
            // The engine never auto-accepts the leak risk — that flag
            // must be set by a UI prompt before the orchestrator
            // re-adds the job. So we pass false here; if the magnet is
            // private-suspect with no trackers, GuardOrThrow upstream
            // would already have rejected the call.
            UserAcceptedLeakRisk: false);
        var decision = MagnetIngestor.Decide(parsed, hint);

        var settingsBuilder = new TorrentSettingsBuilder();
        // Private-suspect: force tracker-only metadata fetch even at the
        // per-torrent level. This is belt-and-suspenders alongside the
        // private-tracker policy applied after metadata arrives.
        if (decision == MagnetIngestionDecision.TrackerOnly)
        {
            settingsBuilder.AllowDht = false;
            settingsBuilder.AllowPeerExchange = false;
        }

        var magnetLink = MonoTorrent.MagnetLink.Parse(source.MagnetUri);
        var manager = await _engine!.AddAsync(magnetLink, downloadDir, settingsBuilder.ToSettings()).ConfigureAwait(false);

        // Wait for BEP-9 metadata exchange to complete before the
        // executor moves the job into the lifecycle proper. Cap the
        // wait so a magnet that never resolves doesn't hang the
        // worker forever.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DefaultOptions.MagnetMetadataTimeout);
        try
        {
            await manager.WaitForMetadataAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Magnet metadata fetch timed out after {DefaultOptions.MagnetMetadataTimeout}. " +
                $"Infohash {parsed.InfohashV1Hex ?? parsed.InfohashV2Hex} — try a .torrent file " +
                "or a magnet with active trackers.");
        }

        // Now that metadata is in, re-apply private-tracker policy
        // based on the resolved torrent.IsPrivate (the magnet's
        // LooksPrivate heuristic isn't authoritative — the actual
        // private bit lives in the info dict).
        var torrent = manager.Torrent
            ?? throw new InvalidOperationException("Metadata received but Torrent property is null.");
        if (torrent.IsPrivate && decision != MagnetIngestionDecision.TrackerOnly)
        {
            // The magnet didn't look private but the torrent IS. We
            // ALREADY exchanged the infohash with DHT/PEX for the
            // metadata fetch — that horse has bolted. Lock down for
            // the download phase by retroactively applying the
            // private-tracker policy.
            var lockdown = new TorrentSettingsBuilder(manager.Settings);
            ApplyPolicyIfPrivate(lockdown, isPrivate: true, options);
            await manager.UpdateSettingsAsync(lockdown.ToSettings()).ConfigureAwait(false);
        }

        return RegisterAndBuildHandle(manager, torrent.Name, torrent.Size, torrent.IsPrivate, ct);
    }

    private TorrentJobHandle RegisterAndBuildHandle(
        MonoTorrentSource manager, string displayName, long totalBytes, bool isPrivate, CancellationToken ct)
    {
        var jobId = manager.InfoHashes.V1OrV2.ToHex();
        lock (_gate) _managers[jobId] = manager;
        WireManagerEvents(jobId, manager);

        // Don't await — fire-and-forget the event so the synchronous
        // return path stays predictable. Channel is unbounded so this
        // can't block.
        _events.Writer.TryWrite(new TorrentEngineEvent.Added(
            jobId, DateTimeOffset.UtcNow, displayName));

        var infohashV1 = manager.InfoHashes.V1?.ToHex();
        var infohashV2 = manager.InfoHashes.V2?.ToHex();
        return new TorrentJobHandle(
            JobId: jobId,
            DisplayName: displayName,
            InfohashV1Hex: infohashV1 ?? string.Empty,
            InfohashV2Hex: infohashV2,
            TotalBytes: totalBytes,
            IsPrivate: isPrivate);
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

    /// <summary>
    /// Applies <see cref="PrivateTrackerPolicy.Required"/> overrides to
    /// a per-torrent settings builder when the torrent is private.
    /// This is the SINGLE PATH any add operation goes through; a
    /// private torrent cannot bypass this even if the user has the
    /// global DHT/PEX/LSD flags on.
    /// </summary>
    private static void ApplyPolicyIfPrivate(
        TorrentSettingsBuilder settingsBuilder,
        bool isPrivate,
        TorrentAddOptions options)
    {
        var effective = options.IsPrivateOverride ?? isPrivate;
        if (!effective) return;

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
    int MaxDownloadBytesPerSecond = 0)
{
    /// <summary>
    /// How long to wait for BEP-9 metadata exchange when adding a
    /// magnet link before giving up. 5 minutes is generous enough for
    /// poorly-seeded magnets but short enough that a misconfigured
    /// (no-peer) magnet doesn't permanently wedge a worker slot.
    /// </summary>
    public TimeSpan MagnetMetadataTimeout { get; init; } = TimeSpan.FromMinutes(5);
}
