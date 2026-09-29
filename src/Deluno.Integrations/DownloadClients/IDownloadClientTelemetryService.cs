using Deluno.Jobs.Contracts;

namespace Deluno.Integrations.DownloadClients;

public interface IDownloadClientTelemetryService
{
    Task<DownloadTelemetryOverview> GetOverviewAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The same Deluno-only view as <see cref="GetOverviewAsync"/>, plus the
    /// paths of the items Deluno did not send
    /// (<see cref="DownloadClientTelemetrySnapshot.OutsideSourcePaths"/>).
    /// Used only by the leftover-download scan (#958/#1062): those items are
    /// never listed, counted or acted on, but their files are really on disk
    /// and must never be swept up.
    /// </summary>
    Task<IReadOnlyList<DownloadClientTelemetrySnapshot>> GetSnapshotsForLeftoverScanAsync(CancellationToken cancellationToken);

    Task<DownloadClientActionResult> ExecuteActionAsync(
        string clientId,
        DownloadClientActionRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// The same action, for a caller that already holds the dispatch Deluno
    /// recorded for this download (a forced re-download, an archived or
    /// still-id-less dispatch). Server-side only: the dispatch is trusted only
    /// when it is for this client and matches the item - by its recorded id, or
    /// (when it never got one) by the release name the client is showing under
    /// that id - never because a caller said so (#1062).
    /// </summary>
    Task<DownloadClientActionResult> ExecuteActionForDispatchAsync(
        string clientId,
        DownloadClientActionRequest request,
        DownloadDispatchItem dispatch,
        CancellationToken cancellationToken)
        => ExecuteActionAsync(clientId, request, cancellationToken);

    /// <summary>
    /// A Deluno download can be sent before the client hands back its id, and
    /// then never record one. Gives the newest such dispatch for this release
    /// the client's id, so the download stays Deluno's own. False when there is
    /// no such dispatch.
    /// </summary>
    Task<bool> AdoptClientItemIdAsync(
        string clientId,
        string queueItemId,
        string releaseName,
        CancellationToken cancellationToken)
        => Task.FromResult(false);

    Task<DownloadCleanupPreview?> PreviewCleanupAsync(
        string clientId,
        string queueItemId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies the explicitly enabled health policy to the supplied, freshly
    /// observed queue. This is invoked by the worker only, never by a browser
    /// telemetry refresh.
    /// </summary>
    Task<DownloadHealthRemediationReport> RunConfiguredHealthRemediationAsync(
        DownloadTelemetryOverview overview,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes a completed item and its data because its sharing rule says the
    /// obligation is discharged (#288).
    ///
    /// Deliberately not an action on the queue-actions endpoint. That surface
    /// normalises a caller-supplied verb, so anything reachable through it is
    /// reachable from a browser, and reclaiming carries its own authorisation:
    /// the user chose a sharing mode that tidies up, and the worker only offers
    /// items Deluno itself dispatched. Routing it through the public action
    /// path would mean either a hole around the manual-removal opt-in or a
    /// feature that silently does nothing until an unrelated toggle is found.
    ///
    /// Invoked by the worker only, never by a browser.
    /// </summary>
    Task<DownloadClientActionResult> ReclaimCompletedAsync(
        string clientId,
        string queueItemId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The Queue's "why" behind removing a download (cleanup sheet J1/J2,
    /// #924): one of <see cref="QueueRemovalReasons"/>, each doing everything
    /// that choice means in one call — the client action, the blocklist entry
    /// and replacement search for "it's bad", nothing more for "just stop
    /// tracking it" — and writing it to Activity in plain words.
    /// </summary>
    Task<QueueRemovalResult> RemoveWithReasonAsync(
        string clientId,
        string queueItemId,
        string reason,
        CancellationToken cancellationToken);

    /// <summary>
    /// The same removal with the choices given as flags, the way Radarr's and
    /// Sonarr's queue delete takes them (#1022): <paramref name="removeFromClient"/>
    /// deletes the download and its files from the client (otherwise Deluno only
    /// forgets it), <paramref name="blocklist"/> refuses the release everywhere
    /// and searches for another unless <paramref name="skipRedownload"/>. Writes
    /// Activity like every other removal.
    /// </summary>
    Task<QueueRemovalResult> RemoveWithOptionsAsync(
        string clientId,
        string queueItemId,
        bool removeFromClient,
        bool blocklist,
        bool skipRedownload,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("This telemetry service does not remove by flags.");

    /// <summary>
    /// The one ownership guard (#1062, #933): whether Deluno itself sent this
    /// download. Every path that reorders, force-starts or removes a download
    /// asks it, so a download added by hand or by another tool is never
    /// touched, and the message says so in plain words.
    /// </summary>
    Task<DownloadOwnership> CheckOwnershipAsync(string clientId, string queueItemId, CancellationToken cancellationToken)
        => Task.FromResult(new DownloadOwnership(true, null));

    /// <summary>
    /// Clears a client's own queue-level warnings (#813) — SABnzbd's
    /// mode=warnings&amp;name=clear — so it does not keep reporting one Deluno
    /// already showed and the person handled. False for a client whose
    /// protocol has no such concept, or that could not be reached.
    /// </summary>
    Task<bool> ClearClientWarningsAsync(string clientId, CancellationToken cancellationToken);
}
