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

    /// <summary>
    /// How urgently this release should be fetched relative to the rest of the
    /// client's queue: negative for low, zero (or unset) for normal, positive
    /// for high (#663). Each adapter maps this onto its own native scale —
    /// NZBGet's <c>append</c> priority is a continuous integer and SABnzbd's
    /// <c>addurl</c> priority is a small discrete enum — so the request carries
    /// intent, not a client-specific number.
    /// </summary>
    public int? Priority { get; init; }

    /// <summary>
    /// The archive password for a password-protected release, when the
    /// release or its indexer actually provided one. Never fabricated: absent
    /// unless something upstream supplied it, because a wrong password sent
    /// to a client that did not need one is worse than sending none.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// The torrent info-hash or NZB id the indexer's own listing named for this
    /// release (a Torznab `infohash` attribute, say), carried from the search
    /// result. The grab service prefers the hash it works out from the magnet or
    /// the file it sends, and uses this one only when it agrees or when there is
    /// nothing else to go on (#1062).
    /// </summary>
    public string? ReleaseHash { get; init; }

    /// <summary>
    /// The torrent's info-hash, lower-case hex, known BEFORE the client is asked
    /// anything (#1062): read from the magnet, or worked out from the .torrent
    /// bytes. Filled in by the grab service and never read from a caller. Every
    /// torrent client files the download under it, so the grab is known by this
    /// id and no client is asked to name it.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? KnownInfoHash { get; init; }

    /// <summary>
    /// The folder, written the way the client sees it, this grab must be saved
    /// into (#1073). Filled in by the grab service from the library's downloads
    /// folder for a client that has no category folders of its own (Transmission,
    /// Deluge); never read from a caller, so an API body cannot set it.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SaveFolder { get; init; }
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

    /// <summary>
    /// Whether this success is a torrent client saying it already holds this
    /// release — Deluno follows the copy it already has instead of sending
    /// another (#877, cleanup sheet B3). Kept distinct from an ordinary new
    /// send so the caller can still write Activity in plain words about what
    /// actually happened, even though nothing here failed.
    /// </summary>
    public bool IsDuplicateFollow { get; init; }
}
