namespace Deluno.Integrations.DownloadClients;

public sealed record DownloadClientGrabRequest(
    string ReleaseName,
    string DownloadUrl,
    string MediaType,
    string? Category,
    string? IndexerName,
    string? DispatchId = null);

public sealed record DownloadClientGrabResult(
    string ClientId,
    string ReleaseName,
    bool Succeeded,
    string Status,
    string Message,
    int? ResponseCode = null,
    string? FailureCode = null,
    string? ResponseJson = null);
