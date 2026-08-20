using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Deluno.Connections.Contracts;

namespace Deluno.Integrations.DownloadClients.Clients;

public sealed class DelugeDownloadClient(IHttpClientFactory httpClientFactory) : DownloadClientBase
{
    public override string Protocol => "deluge";
    public override DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, false, true, true, true, true, "password");

    public override async Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.GrabFailure(client, request, "Download client address is missing.");
        var http = CreateHttp();
        await LoginAsync(http, baseUri, client, cancellationToken);
        await DownloadClientHelpers.PostJsonAsync<DelugeResponse<object>>(http, new Uri(baseUri, "json"), new DelugeRequest("core.add_torrent_url", [request.DownloadUrl, new Dictionary<string, object>()], 2), cancellationToken);
        return DownloadClientHelpers.GrabSuccess(client, request, "Release URL sent to Deluge.");
    }

    public override async Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return null;
        var http = CreateHttp();
        await LoginAsync(http, baseUri, client, cancellationToken);
        var payload = new DelugeRequest("web.update_ui", [new[] { "name", "state", "progress", "download_payload_rate", "eta", "total_size", "total_done", "num_peers", "time_added", "label", "message", "save_path" }, new Dictionary<string, object>()], 2);
        var response = await DownloadClientHelpers.PostJsonAsync<DelugeResponse<DelugeUpdateResult>>(http, new Uri(baseUri, "json"), payload, cancellationToken);
        var queue = (response?.Result?.Torrents ?? new Dictionary<string, DelugeTorrent>()).Select(pair =>
        {
            var item = pair.Value;
            return new DownloadQueueItem(pair.Key, client.Id, client.Name, client.Protocol, DownloadClientHelpers.InferMediaType(client, item.Label),
                DownloadClientHelpers.CleanReleaseTitle(item.Name ?? "Unknown Deluge item"), item.Name ?? "Unknown Deluge item", item.Label ?? string.Empty,
                NormalizeStatus(item.State, item.Progress), Math.Clamp(Math.Round(item.Progress ?? 0, 1), 0, 100), Math.Round((item.DownloadPayloadRate ?? 0) / 1_000_000d, 1),
                Math.Max(0, Convert.ToInt32(item.Eta ?? 0)), Convert.ToInt64(item.TotalSize ?? 0), Convert.ToInt64(item.TotalDone ?? 0), item.NumPeers ?? 0, "Deluge",
                string.IsNullOrWhiteSpace(item.Message) ? null : item.Message, DownloadClientHelpers.FromUnix(Convert.ToInt64(item.TimeAdded ?? 0)), DownloadClientHelpers.ResolveDownloadPath(item.SavePath, item.Name));
        }).ToArray();
        return CreateSnapshot(client, queue, capturedUtc, "healthy", $"Connected to Deluge at {baseUri.Host}:{baseUri.Port}.");
    }

    public override async Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
    {
        var method = action switch { "pause" => "core.pause_torrent", "resume" => "core.resume_torrent", "delete" => "core.remove_torrent", "recheck" => "core.force_recheck", _ => null };
        if (method is null) return DownloadClientHelpers.Unsupported(client, queueItemId, action, "Deluge");
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.MissingAddress(client, queueItemId, action);
        var http = CreateHttp();
        await LoginAsync(http, baseUri, client, cancellationToken);
        object[] parameters = action == "delete" ? [new[] { queueItemId }, false] : [new[] { queueItemId }];
        await DownloadClientHelpers.PostJsonAsync<DelugeResponse<object>>(http, new Uri(baseUri, "json"), new DelugeRequest(method, parameters, 3), cancellationToken);
        return DownloadClientHelpers.ActionSuccess(client, queueItemId, action, "Deluge action sent.");
    }

    public override string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
        => DownloadClientHelpers.NormalizeTextStatus(nativeStatus, progress);

    private HttpClient CreateHttp()
    {
        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);
        return http;
    }

    private static async Task LoginAsync(HttpClient http, Uri baseUri, DownloadClientItem client, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(client.Secret)) return;
        await DownloadClientHelpers.PostJsonAsync<DelugeResponse<object>>(http, new Uri(baseUri, "json"), new DelugeRequest("auth.login", [client.Secret], 1), cancellationToken);
    }

    private sealed record DelugeRequest([property: JsonPropertyName("method")] string Method, [property: JsonPropertyName("params")] object[] Params, [property: JsonPropertyName("id")] int Id);
    private sealed record DelugeResponse<T>([property: JsonPropertyName("result")] T? Result);
    private sealed record DelugeUpdateResult([property: JsonPropertyName("torrents")] IReadOnlyDictionary<string, DelugeTorrent>? Torrents);
    private sealed record DelugeTorrent([property: JsonPropertyName("name")] string? Name, [property: JsonPropertyName("state")] string? State, [property: JsonPropertyName("progress")] double? Progress, [property: JsonPropertyName("download_payload_rate")] double? DownloadPayloadRate, [property: JsonPropertyName("eta")] double? Eta, [property: JsonPropertyName("total_size")] double? TotalSize, [property: JsonPropertyName("total_done")] double? TotalDone, [property: JsonPropertyName("num_peers")] int? NumPeers, [property: JsonPropertyName("time_added")] double? TimeAdded, [property: JsonPropertyName("label")] string? Label, [property: JsonPropertyName("message")] string? Message, [property: JsonPropertyName("save_path")] string? SavePath);
}
