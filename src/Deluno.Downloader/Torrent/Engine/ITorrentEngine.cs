namespace Deluno.Downloader.Torrent.Engine;

/// <summary>
/// Stable internal API around the MonoTorrent ClientEngine. The rest
/// of Deluno never imports MonoTorrent types directly — they all go
/// through this seam. If we ever swap implementations (or vendor a
/// fork) callers don't change.
///
/// All operations are async; the wrapper marshals MonoTorrent's
/// event-driven model onto our <see cref="JobLifecycleState"/> machine
/// from the shared layer.
/// </summary>
public interface ITorrentEngine : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);

    Task<TorrentJobHandle> AddAsync(
        TorrentSource source,
        TorrentAddOptions options,
        CancellationToken ct);

    Task PauseAsync(string jobId, CancellationToken ct);
    Task ResumeAsync(string jobId, CancellationToken ct);
    Task RemoveAsync(string jobId, bool deleteData, CancellationToken ct);
    Task ForceRecheckAsync(string jobId, CancellationToken ct);

    /// <summary>
    /// Hot stream of lifecycle events for active torrents. Consumers
    /// (the orchestrator + the SignalR adapter) subscribe once per
    /// engine startup.
    /// </summary>
    IAsyncEnumerable<TorrentEngineEvent> Events { get; }
}

/// <summary>
/// Sources a torrent can be added from. Magnet links require BEP-9
/// metadata exchange (DHT/PEX/tracker) BEFORE we know whether the
/// torrent is private — handled by <c>MagnetIngestor</c> with the
/// leak-window guard.
/// </summary>
public abstract record TorrentSource
{
    public sealed record Magnet(string MagnetUri) : TorrentSource;
    public sealed record TorrentFile(string Path) : TorrentSource;
    public sealed record TorrentBytes(byte[] Content) : TorrentSource;
}

public sealed record TorrentAddOptions(
    string? Category = null,
    string? DownloadDir = null,
    int Priority = 0,
    double? RatioTarget = null,
    TimeSpan? SeedTimeTarget = null,
    bool? IsPrivateOverride = null,
    string? Password = null);

public sealed record TorrentJobHandle(
    string JobId,
    string DisplayName,
    string InfohashV1Hex,
    string? InfohashV2Hex,
    long TotalBytes,
    bool IsPrivate);

public abstract record TorrentEngineEvent(string JobId, DateTimeOffset At)
{
    public sealed record Added(string JobId, DateTimeOffset At, string DisplayName) : TorrentEngineEvent(JobId, At);
    public sealed record MetadataResolved(string JobId, DateTimeOffset At, long TotalBytes, bool IsPrivate) : TorrentEngineEvent(JobId, At);
    public sealed record Progress(string JobId, DateTimeOffset At, double Percent, long DownloadedBytes, long UploadedBytes, int Peers) : TorrentEngineEvent(JobId, At);
    public sealed record StateChanged(string JobId, DateTimeOffset At, string NewState) : TorrentEngineEvent(JobId, At);
    public sealed record Completed(string JobId, DateTimeOffset At, string OutputPath) : TorrentEngineEvent(JobId, At);
    public sealed record Failed(string JobId, DateTimeOffset At, string Reason) : TorrentEngineEvent(JobId, At);
}
