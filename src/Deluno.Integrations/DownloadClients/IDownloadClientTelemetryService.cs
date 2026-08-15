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
}
