namespace Deluno.Integrations.DownloadClients;

public interface IDownloadClientTelemetryService
{
    Task<DownloadTelemetryOverview> GetOverviewAsync(CancellationToken cancellationToken);

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
}
