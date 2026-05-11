namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// Generic webhook payload accepted from any download client.
/// Users configure their client's completion script or notification URL to POST this to
/// /api/download-clients/{clientId}/webhook.
/// </summary>
public sealed record DownloadClientWebhookRequest(
    string Event,
    string? DispatchId,
    string? Hash,
    string? Name,
    string? SavePath,
    long? SizeBytes,
    string? FailureReason);

public sealed record DownloadClientWebhookResult(
    bool Accepted,
    string? DispatchId,
    string Message);
