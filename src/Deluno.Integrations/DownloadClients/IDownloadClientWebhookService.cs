namespace Deluno.Integrations.DownloadClients;

public interface IDownloadClientWebhookService
{
    Task<DownloadClientWebhookResult> HandleAsync(
        string clientId,
        DownloadClientWebhookRequest request,
        CancellationToken cancellationToken);
}
