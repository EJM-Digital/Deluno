using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Deluno.Connections.Contracts;

namespace Deluno.Integrations.DownloadClients.Clients;

public sealed class TransmissionDownloadClient(IHttpClientFactory httpClientFactory) : DownloadClientBase
{
    public override string Protocol => "transmission";
    public override DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, false, true, true, true, true, "basic");

    public override async Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.GrabFailure(client, request, "Download client address is missing.");
        await SendAsync(client, baseUri, new TransmissionRequest("torrent-add", new() { ["filename"] = request.DownloadUrl }), cancellationToken);
        return DownloadClientHelpers.GrabSuccess(client, request, "Release URL sent to Transmission.");
    }

    public override async Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return null;
        var response = await SendAsync(client, baseUri, new TransmissionRequest("torrent-get", new()
        {
            ["fields"] = new[] { "id", "name", "status", "percentDone", "rateDownload", "rateUpload", "eta", "totalSize", "downloadedEver", "peersConnected", "addedDate", "doneDate", "downloadDir", "labels", "error", "errorString" }
        }), cancellationToken);
        var queue = (response.Arguments?.Torrents ?? []).Select(item => new DownloadQueueItem(
            item.Id?.ToString(CultureInfo.InvariantCulture) ?? item.Name ?? Guid.CreateVersion7().ToString("N"), client.Id, client.Name, client.Protocol,
            DownloadClientHelpers.InferMediaType(client, item.Labels?.FirstOrDefault()), DownloadClientHelpers.CleanReleaseTitle(item.Name ?? "Unknown Transmission item"),
            item.Name ?? "Unknown Transmission item", item.Labels?.FirstOrDefault() ?? string.Empty, NormalizeStatus(item.Status?.ToString(CultureInfo.InvariantCulture), item.PercentDone, item.Error, item.ErrorString),
            Math.Clamp(Math.Round((item.PercentDone ?? 0) * 100, 1), 0, 100), Math.Round((item.RateDownload ?? 0) / 1_000_000d, 1), Math.Max(0, item.Eta ?? 0),
            item.TotalSize ?? 0, item.DownloadedEver ?? 0, item.PeersConnected ?? 0, "Transmission", string.IsNullOrWhiteSpace(item.ErrorString) ? null : item.ErrorString,
            DownloadClientHelpers.FromUnix(item.AddedDate), DownloadClientHelpers.ResolveDownloadPath(item.DownloadDir, item.Name),
            UploadSpeedMbps: Math.Round((item.RateUpload ?? 0) / 1_000_000d, 1))).ToArray();
        return CreateSnapshot(client, queue, capturedUtc, "healthy", $"Connected to Transmission at {baseUri.Host}:{baseUri.Port}.");
    }

    public override async Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
    {
        var method = action switch { "pause" => "torrent-stop", "resume" => "torrent-start", "delete" or "delete-with-data" => "torrent-remove", "recheck" => "torrent-verify", _ => null };
        if (method is null) return DownloadClientHelpers.Unsupported(client, queueItemId, action, "Transmission");
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.MissingAddress(client, queueItemId, action);
        var arguments = new Dictionary<string, object> { ["ids"] = new[] { DownloadClientHelpers.ParseId(queueItemId) } };
        // The whole point of the distinction: "delete" forgets the torrent and
        // leaves the file, "delete-with-data" asks Transmission to remove both.
        // Deluno never deletes a shared file itself (#287).
        if (action is "delete" or "delete-with-data") arguments["delete-local-data"] = action == "delete-with-data";
        await SendAsync(client, baseUri, new TransmissionRequest(method, arguments), cancellationToken);
        return DownloadClientHelpers.ActionSuccess(client, queueItemId, action, "Transmission action sent.");
    }

    public override string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
    {
        if (errorCode is > 0 || !string.IsNullOrWhiteSpace(errorMessage)) return DownloadQueueStatuses.Stalled;
        if ((progress ?? 0) >= 1) return DownloadQueueStatuses.ImportReady;
        return int.TryParse(nativeStatus, out var status) && status == 4 ? DownloadQueueStatuses.Downloading : DownloadQueueStatuses.Queued;
    }

    private async Task<TransmissionResponse> SendAsync(DownloadClientItem client, Uri baseUri, TransmissionRequest payload, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);
        DownloadClientHelpers.AddBasicAuth(http, client);
        var uri = new Uri(baseUri, "transmission/rpc");
        using var first = await http.PostAsJsonAsync(uri, payload, cancellationToken);
        if ((int)first.StatusCode == 409 && first.Headers.TryGetValues("X-Transmission-Session-Id", out var values))
        {
            http.DefaultRequestHeaders.Remove("X-Transmission-Session-Id");
            http.DefaultRequestHeaders.Add("X-Transmission-Session-Id", values.First());
            using var second = await http.PostAsJsonAsync(uri, payload, cancellationToken);
            second.EnsureSuccessStatusCode();
            return await second.Content.ReadFromJsonAsync<TransmissionResponse>(cancellationToken) ?? new(null);
        }
        first.EnsureSuccessStatusCode();
        return await first.Content.ReadFromJsonAsync<TransmissionResponse>(cancellationToken) ?? new(null);
    }

    private sealed record TransmissionRequest([property: JsonPropertyName("method")] string Method, [property: JsonPropertyName("arguments")] Dictionary<string, object> Arguments);
    private sealed record TransmissionResponse([property: JsonPropertyName("arguments")] TransmissionArguments? Arguments);
    private sealed record TransmissionArguments([property: JsonPropertyName("torrents")] IReadOnlyList<TransmissionTorrent>? Torrents);
    private sealed record TransmissionTorrent([property: JsonPropertyName("id")] int? Id, [property: JsonPropertyName("name")] string? Name, [property: JsonPropertyName("status")] int? Status, [property: JsonPropertyName("percentDone")] double? PercentDone, [property: JsonPropertyName("rateDownload")] long? RateDownload, [property: JsonPropertyName("rateUpload")] long? RateUpload, [property: JsonPropertyName("eta")] int? Eta, [property: JsonPropertyName("totalSize")] long? TotalSize, [property: JsonPropertyName("downloadedEver")] long? DownloadedEver, [property: JsonPropertyName("peersConnected")] int? PeersConnected, [property: JsonPropertyName("addedDate")] long? AddedDate, [property: JsonPropertyName("downloadDir")] string? DownloadDir, [property: JsonPropertyName("labels")] IReadOnlyList<string>? Labels, [property: JsonPropertyName("error")] int? Error, [property: JsonPropertyName("errorString")] string? ErrorString);
}
