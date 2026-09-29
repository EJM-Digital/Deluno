namespace Deluno.Integrations.DownloadClients;

public interface IDownloadClientTelemetryService
{
    Task<DownloadTelemetryOverview> GetOverviewAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Every client's queue and history exactly as the client reports it,
    /// ownership untouched -- unlike <see cref="GetOverviewAsync"/>, this
    /// keeps items another tool, or a person, added to the client. Used only
    /// by the leftover-download scan (#958/#933 M2): a foreign item is really
    /// on disk and must still hold its file, even though it never appears on
    /// the Queue screen.
    /// </summary>
    Task<IReadOnlyList<DownloadClientTelemetrySnapshot>> GetUnfilteredSnapshotsAsync(CancellationToken cancellationToken);

    Task<DownloadClientActionResult> ExecuteActionAsync(
        string clientId,
        DownloadClientActionRequest request,
        CancellationToken cancellationToken);

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
    /// Clears a client's own queue-level warnings (#813) — SABnzbd's
    /// mode=warnings&amp;name=clear — so it does not keep reporting one Deluno
    /// already showed and the person handled. False for a client whose
    /// protocol has no such concept, or that could not be reached.
    /// </summary>
    Task<bool> ClearClientWarningsAsync(string clientId, CancellationToken cancellationToken);
}
