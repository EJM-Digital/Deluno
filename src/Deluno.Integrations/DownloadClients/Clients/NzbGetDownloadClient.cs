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
        if (baseUri is null) return CreateConfigurationSnapshot(client, capturedUtc, "Download client address is missing.");
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

    /// <summary>
    /// One NZBGet command per Deluno verb, and every one of them acting on the
    /// download the caller named.
    ///
    /// <para><b>Two of these used to be wrong in the same way: they did not
    /// match what was asked.</b> `pause` and `resume` sent
    /// <c>pausedownload</c> and <c>resumedownload</c>, which are NZBGet's
    /// global switches — asked to pause one download, Deluno stopped the whole
    /// client and reported success. And `delete` and `delete-with-data` both
    /// sent <c>GroupDelete</c>, so the distinction the two verbs exist to draw
    /// was silently dropped: a caller asking to take the files got the same
    /// request as one asking to leave them.</para>
    ///
    /// <para>The mapping is spelled out one arm at a time now, rather than
    /// sharing arms with <c>or</c>, so a verb that has no distinct command has
    /// to say so in the open.</para>
    ///
    /// <para>Written against NZBGet's documented <c>editqueue</c> command set.
    /// The lab has no NZBGet instance, so unlike the qBittorrent path this is
    /// not confirmed against a live server.</para>
    /// </summary>
    public override async Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.MissingAddress(client, queueItemId, action);

        if (!DownloadClientHelpers.TryParseId(queueItemId, out var id))
        {
            return DownloadClientHelpers.UnreadableId(client, queueItemId, action, "NZBGet");
        }

        var http = CreateHttp(client);
        var endpoint = new Uri(baseUri, "jsonrpc");

        async Task Edit(string command) =>
            await DownloadClientHelpers.PostJsonAsync<NzbGetResponse<object>>(
                http, endpoint, new NzbGetRequest("editqueue", [command, 0, "", new[] { id }]), cancellationToken);

        switch (action)
        {
            case DownloadClientActions.Pause:
                await Edit("GroupPause");
                return Sent(client, queueItemId, action, "Paused this download in NZBGet.");

            case DownloadClientActions.Resume:
                await Edit("GroupResume");
                return Sent(client, queueItemId, action, "Resumed this download in NZBGet.");

            // Out of the queue, files left where they are.
            case DownloadClientActions.Delete:
                await Edit("GroupDelete");
                return Sent(client, queueItemId, action, "Removed this download from NZBGet, leaving its files.");

            // The same, and the files with it. GroupFinalDelete is the only
            // command that does both; GroupDelete does not.
            case DownloadClientActions.DeleteWithData:
                await Edit("GroupFinalDelete");
                return Sent(client, queueItemId, action, "Removed this download from NZBGet, along with its files.");

            // Two requests, because NZBGet keeps the thing that refuses a
            // release somewhere the first one does not reach. The history entry
            // is what its duplicate check reads, and HistoryFinalDelete is the
            // only command that removes it — HistoryDelete hides it, and a
            // hidden entry still refuses the release.
            case DownloadClientActions.Forget:
                await Edit("GroupFinalDelete");
                await Edit("HistoryFinalDelete");
                return Sent(client, queueItemId, action, "Removed this download and its history entry from NZBGet, so it will accept this release again.");

            // NZBGet verifies a download itself and exposes no per-item recheck,
            // so this is refused rather than quietly mapped to something else.
            default:
                return DownloadClientHelpers.Unsupported(client, queueItemId, action, "NZBGet");
        }
    }

    private static DownloadClientActionResult Sent(DownloadClientItem client, string queueItemId, string action, string message)
        => DownloadClientHelpers.ActionSuccess(client, queueItemId, action, message);

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
        var response = await DownloadClientHelpers.PostJsonAsync<NzbGetResponse<IReadOnlyList<NzbGetHistoryItem>>>(http, new Uri(baseUri, "jsonrpc"), new NzbGetRequest("history", []), cancellationToken);
        return (response?.Result ?? []).Select(item => new DownloadClientHistoryItem(item.NzbId.ToString(CultureInfo.InvariantCulture), client.Id, client.Name, client.Protocol,
            DownloadClientHelpers.InferMediaType(client, item.Category), DownloadClientHelpers.CleanReleaseTitle(item.NzbName ?? item.Name ?? "Unknown NZBGet history item"), item.NzbName ?? item.Name ?? "Unknown NZBGet history item",
            item.Category ?? string.Empty, NormalizeOutcome(item.Status ?? string.Empty), "NZBGet", item.FileSizeHi * 1_000_000L, DownloadClientHelpers.FromUnix(item.HistoryTime), QueueError(item.Status), DownloadClientHelpers.ResolveDownloadPath(item.DestDir, item.NzbName ?? item.Name),
            HistorySource: "native",
            ExternalId: item.NzbId.ToString(CultureInfo.InvariantCulture))).ToArray();
    }

    private static async Task<NzbGetStatus?> GetStatusAsync(HttpClient http, Uri baseUri, CancellationToken cancellationToken)
    {
        return (await DownloadClientHelpers.PostJsonAsync<NzbGetResponse<NzbGetStatus>>(http, new Uri(baseUri, "jsonrpc"), new NzbGetRequest("status", []), cancellationToken))?.Result;
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
