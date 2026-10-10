using Deluno.Connections.Usenet;
using Deluno.Connections.Contracts;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// The protocol-specific boundary for one external download client.
/// Implementations are deliberately stateless; all client configuration is supplied
/// for each call so a singleton can safely serve every configured connection.
/// </summary>
public interface IDownloadClient
{
    string Protocol { get; }

    DownloadClientTelemetryCapabilities Capabilities { get; }

    /// <summary>
    /// True for a client with no category folders of its own that takes the
    /// save folder on each add (Transmission's download-dir, Deluge's
    /// download_location). The grab service then names the library's downloads
    /// folder on every grab (#1073).
    /// </summary>
    bool SetsSaveFolderPerGrab => false;

    /// <summary>
    /// For a Usenet client: whether it has a news server it can download from (#1142). Null for a
    /// client that has no such thing, and null when the client could not be asked.
    /// </summary>
    Task<bool?> HasUsenetProviderAsync(DownloadClientItem client, CancellationToken cancellationToken)
        => Task.FromResult<bool?>(null);

    /// <summary>For a Usenet client: writes the provider to the client's own settings. Null for a client that takes none.</summary>
    Task<UsenetProviderClientResult?> SetUsenetProviderAsync(DownloadClientItem client, UsenetProviderRequest request, CancellationToken cancellationToken)
        => Task.FromResult<UsenetProviderClientResult?>(null);

    Task<DownloadClientGrabResult> GrabAsync(
        DownloadClientItem client,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken);

    Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(
        DownloadClientItem client,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken);

    Task<DownloadClientActionResult> ExecuteActionAsync(
        DownloadClientItem client,
        string action,
        string queueItemId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(
        DownloadClientItem client,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken);

    Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(
        DownloadClientItem client,
        string category,
        CancellationToken cancellationToken);

    /// <summary>
    /// #1240: moves one torrent to a keep-seeding category (qBittorrent) or label (Deluge, Transmission, rTorrent,
    /// uTorrent) and changes nothing else about it: it is never paused, never removed, and its files stay where they
    /// are. The client is asked before and after, so "done" means it now reports the torrent in that category.
    /// A refusal comes back as a failed result with the reason in plain words; nothing is deleted on any path.
    /// A client that cannot do this (Usenet) answers "not supported". <paramref name="currentCategory"/> is the
    /// category or label Deluno knew it by, which Transmission replaces within the torrent's label set.
    /// </summary>
    Task<DownloadClientActionResult> SetCategoryAsync(
        DownloadClientItem client,
        string queueItemId,
        string category,
        string? currentCategory,
        CancellationToken cancellationToken)
        => Task.FromResult(DownloadClientHelpers.Unsupported(client, queueItemId, DownloadClientActions.SetCategory, client.Protocol));

    /// <summary>
    /// #1240: whether a keep-seeding category can be used on this client: ready, missing (the person has to create
    /// it in the client first), or unsupported with the reason (Deluge's Label plugin is off, a Transmission too old
    /// for labels, a Usenet client). Never creates anything.
    /// </summary>
    Task<DownloadClientCategoryCheckResult> CheckKeepSeedingCategoryAsync(
        DownloadClientItem client,
        string category,
        CancellationToken cancellationToken)
        => Task.FromResult(new DownloadClientCategoryCheckResult(
            client.Id, client.Name, category, DownloadClientCategoryStatuses.Unsupported,
            $"{client.Name} is a Usenet client, and only a torrent can keep seeding.", Supported: false, Found: false));

    /// <summary>
    /// The files actually inside one download, as the client's own file
    /// listing reports them (#816) - a relative path per file, not the
    /// release name. Null when this client does not expose a file listing, or
    /// the queue item cannot be found; empty when it does and genuinely holds
    /// no files. Never throws for an ordinary "not supported" answer.
    /// </summary>
    Task<IReadOnlyList<string>?> GetFilesAsync(
        DownloadClientItem client,
        string queueItemId,
        CancellationToken cancellationToken);

    string NormalizeStatus(
        string? nativeStatus,
        double? progress,
        int? errorCode = null,
        string? errorMessage = null);

    /// <summary>
    /// #813: clears a client's own queue-level warnings (SABnzbd's
    /// mode=warnings&amp;name=clear). A default interface method rather than
    /// a required member, so most clients — which have no such concept — and
    /// every existing test double need not implement it.
    /// </summary>
    Task<bool> ClearWarningsAsync(DownloadClientItem client, CancellationToken cancellationToken)
        => Task.FromResult(false);

    /// <summary>
    /// Resumes a client that has been paused as a whole (the snapshot&apos;s
    /// <c>ClientPaused</c>): SABnzbd&apos;s <c>mode=resume</c>, NZBGet&apos;s
    /// <c>resumedownload</c>, <c>resumepost</c> and <c>resumescan</c>. Not the
    /// per-item resume. Torrent clients and the rest report no client-wide
    /// pause, so they answer &quot;not supported&quot;.
    /// </summary>
    Task<ClientResumeResult> ResumeClientAsync(DownloadClientItem client, CancellationToken cancellationToken)
        => Task.FromResult(ClientResumeResult.NotSupported);
}
