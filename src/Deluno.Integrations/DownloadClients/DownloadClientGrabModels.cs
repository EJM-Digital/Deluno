using Deluno.Contracts;

namespace Deluno.Integrations.DownloadClients;

public sealed record DownloadClientGrabRequest(
    string ReleaseName,
    string DownloadUrl,
    string MediaType,
    string? Category,
    string? IndexerName,
    string? DispatchId = null,
    // The indexer the release came from. A native tracker's link is a page or
    // a signed-in download, so the grab service resolves it first (#606).
    string? IndexerId = null)
{
    /// <summary>
    /// A .torrent file's bytes, when the release had to be fetched by Deluno
    /// (a tracker that needs its sign-in to download). A client given these
    /// uploads them instead of adding <see cref="DownloadUrl"/>.
    /// </summary>
    public byte[]? TorrentFile { get; init; }
}

public sealed record DownloadClientGrabResult(
    string ClientId,
    string ReleaseName,
    bool Succeeded,
    string Status,
    string Message,
    int? ResponseCode = null,
    string? FailureCode = null,
    string? ResponseJson = null)
{
    /// <summary>Attributable failure details when dispatch did not succeed.</summary>
    public IntegrationFailure? Failure { get; init; }

    /// <summary>
    /// Stable identity assigned by the download client. Persisting it at grab
    /// time lets native queue/history rows remain one trace even when the item
    /// completes between Deluno polling intervals.
    /// </summary>
    public string? ExternalId { get; init; }
}
