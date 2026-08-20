using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Deluno.Connections.Contracts;

namespace Deluno.Integrations.DownloadClients.Clients;

public sealed class NzbGetDownloadClient(IHttpClientFactory httpClientFactory) : DownloadClientBase
{
    public override string Protocol => "nzbget";
    public override DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, false, true, "basic");

    public override async Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.GrabFailure(client, request, "Download client address is missing.");
        var http = CreateHttp(client);
        await DownloadClientHelpers.PostJsonAsync<NzbGetResponse<object>>(http, new Uri(baseUri, "jsonrpc"), new NzbGetRequest("appendurl", [request.ReleaseName, request.DownloadUrl, DownloadClientHelpers.ResolveCategory(client, request), 0, false, false]), cancellationToken);
        return DownloadClientHelpers.GrabSuccess(client, request, "Release URL sent to NZBGet.");
    }

    public override async Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return null;
        var http = CreateHttp(client);
        var response = await DownloadClientHelpers.PostJsonAsync<NzbGetResponse<IReadOnlyList<NzbGetQueueItem>>>(http, new Uri(baseUri, "jsonrpc"), new NzbGetRequest("listgroups", []), cancellationToken);
        var status = await GetStatusAsync(http, baseUri, cancellationToken);
        var queue = (response?.Result ?? []).Select(item =>
        {
            var size = item.FileSizeHi * 1_000_000L;
            var remaining = item.RemainingSizeHi * 1_000_000L;
            var downloaded = Math.Max(0, size - remaining);
            var progress = size <= 0 ? 0 : Math.Round(downloaded / (double)size * 100, 1);
            return new DownloadQueueItem(item.NzbId.ToString(CultureInfo.InvariantCulture), client.Id, client.Name, client.Protocol,
                DownloadClientHelpers.InferMediaType(client, item.Category), DownloadClientHelpers.CleanReleaseTitle(item.NzbName ?? "Unknown NZBGet item"), item.NzbName ?? "Unknown NZBGet item",
                item.Category ?? string.Empty, NormalizeStatus(item.Status, progress), progress, Math.Round((status?.DownloadRate ?? 0) / 1_000_000d, 1),
                CalculateEta(remaining, status?.DownloadRate), size, downloaded, 0, "NZBGet", QueueError(item.Status), capturedUtc, DownloadClientHelpers.ResolveDownloadPath(item.DestDir, item.NzbName));
        }).ToArray();
        var history = await GetHistoryCoreAsync(http, client, baseUri, capturedUtc, cancellationToken);
        return CreateSnapshot(client, queue, capturedUtc, "healthy", $"Connected to NZBGet at {baseUri.Host}:{baseUri.Port}.", history.Count > 0 ? history : null);
    }

    public override async Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        return baseUri is null ? [] : await GetHistoryCoreAsync(CreateHttp(client), client, baseUri, capturedUtc, cancellationToken);
    }

    public override async Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
    {
        var method = action switch { "pause" => "pausedownload", "resume" => "resumedownload", "delete" => "editqueue", _ => null };
        if (method is null) return DownloadClientHelpers.Unsupported(client, queueItemId, action, "NZBGet");
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.MissingAddress(client, queueItemId, action);
        object[] parameters = action == "delete" ? ["GroupDelete", 0, "", new[] { DownloadClientHelpers.ParseId(queueItemId) }] : [];
        await DownloadClientHelpers.PostJsonAsync<NzbGetResponse<object>>(CreateHttp(client), new Uri(baseUri, "jsonrpc"), new NzbGetRequest(method, parameters), cancellationToken);
        return DownloadClientHelpers.ActionSuccess(client, queueItemId, action, "NZBGet action sent.");
    }

    public override string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null) => DownloadClientHelpers.NormalizeTextStatus(nativeStatus, progress);

    private HttpClient CreateHttp(DownloadClientItem client)
    {
        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);
        DownloadClientHelpers.AddBasicAuth(http, client);
        return http;
    }

    private static async Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryCoreAsync(HttpClient http, DownloadClientItem client, Uri baseUri, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        try
        {
            var response = await DownloadClientHelpers.PostJsonAsync<NzbGetResponse<IReadOnlyList<NzbGetHistoryItem>>>(http, new Uri(baseUri, "jsonrpc"), new NzbGetRequest("history", []), cancellationToken);
            return (response?.Result ?? []).Select(item => new DownloadClientHistoryItem(item.NzbId.ToString(CultureInfo.InvariantCulture), client.Id, client.Name, client.Protocol,
                DownloadClientHelpers.InferMediaType(client, item.Category), DownloadClientHelpers.CleanReleaseTitle(item.NzbName ?? item.Name ?? "Unknown NZBGet history item"), item.NzbName ?? item.Name ?? "Unknown NZBGet history item",
                item.Category ?? string.Empty, NormalizeOutcome(item.Status ?? string.Empty), "NZBGet", item.FileSizeHi * 1_000_000L, DownloadClientHelpers.FromUnix(item.HistoryTime), QueueError(item.Status), DownloadClientHelpers.ResolveDownloadPath(item.DestDir, item.NzbName ?? item.Name))).ToArray();
        }
        catch { return []; }
    }

    private static async Task<NzbGetStatus?> GetStatusAsync(HttpClient http, Uri baseUri, CancellationToken cancellationToken)
    {
        try { return (await DownloadClientHelpers.PostJsonAsync<NzbGetResponse<NzbGetStatus>>(http, new Uri(baseUri, "jsonrpc"), new NzbGetRequest("status", []), cancellationToken))?.Result; }
        catch { return null; }
    }

    private static int CalculateEta(long remainingBytes, long? bytesPerSecond) => remainingBytes <= 0 || bytesPerSecond is null or <= 0 ? 0 : Convert.ToInt32(Math.Clamp(remainingBytes / (double)bytesPerSecond.Value, 0, int.MaxValue));
    private static string? QueueError(string? status) => string.IsNullOrWhiteSpace(status) ? null : status.Contains("fail", StringComparison.OrdinalIgnoreCase) || status.Contains("error", StringComparison.OrdinalIgnoreCase) || status.Contains("stall", StringComparison.OrdinalIgnoreCase) ? status : null;
    private static string NormalizeOutcome(string value) => value.Equals("completed", StringComparison.OrdinalIgnoreCase) || value.Equals("succeeded", StringComparison.OrdinalIgnoreCase) || value.Equals("success", StringComparison.OrdinalIgnoreCase) ? DownloadQueueStatuses.Completed : value.Contains("fail", StringComparison.OrdinalIgnoreCase) || value.Contains("error", StringComparison.OrdinalIgnoreCase) ? "failed" : value.Contains("import", StringComparison.OrdinalIgnoreCase) ? DownloadQueueStatuses.ImportReady : value.Length == 0 ? "unknown" : value.Trim().ToLowerInvariant();

    private sealed record NzbGetRequest([property: JsonPropertyName("method")] string Method, [property: JsonPropertyName("params")] object[] Params);
    private sealed record NzbGetResponse<T>([property: JsonPropertyName("result")] T? Result);
    private sealed record NzbGetQueueItem([property: JsonPropertyName("NZBID")] int NzbId, [property: JsonPropertyName("NZBName")] string? NzbName, [property: JsonPropertyName("Category")] string? Category, [property: JsonPropertyName("Status")] string? Status, [property: JsonPropertyName("FileSizeHi")] long FileSizeHi, [property: JsonPropertyName("RemainingSizeHi")] long RemainingSizeHi, [property: JsonPropertyName("DestDir")] string? DestDir);
    private sealed record NzbGetHistoryItem([property: JsonPropertyName("NZBID")] int NzbId, [property: JsonPropertyName("NZBName")] string? NzbName, [property: JsonPropertyName("Name")] string? Name, [property: JsonPropertyName("Category")] string? Category, [property: JsonPropertyName("Status")] string? Status, [property: JsonPropertyName("FileSizeHi")] long FileSizeHi, [property: JsonPropertyName("HistoryTime")] long? HistoryTime, [property: JsonPropertyName("DestDir")] string? DestDir);
    private sealed record NzbGetStatus([property: JsonPropertyName("DownloadRate")] long? DownloadRate);
}
