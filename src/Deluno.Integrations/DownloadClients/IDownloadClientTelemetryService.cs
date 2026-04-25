namespace Deluno.Integrations.DownloadClients;

public interface IDownloadClientTelemetryService
{
    Task<DownloadTelemetryOverview> GetOverviewAsync(CancellationToken cancellationToken);

    Task<DownloadClientActionResult> ExecuteActionAsync(
        string clientId,
        DownloadClientActionRequest request,
        CancellationToken cancellationToken);
}
