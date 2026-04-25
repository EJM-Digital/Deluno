namespace Deluno.Integrations.DownloadClients;

public interface IDownloadClientGrabService
{
    Task<DownloadClientGrabResult> GrabAsync(
        string clientId,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken);
}
