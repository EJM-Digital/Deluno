using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Connections.Contracts;

namespace Deluno.Integrations.DownloadClients.Clients;

public sealed class UTorrentDownloadClient : DownloadClientBase
{
    public override string Protocol => "utorrent";
    public override DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, false, true, true, true, false, "basic-token");

    public override async Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.GrabFailure(client, request, "Download client address is missing.");
        using var http = CreateHttp(client, baseUri, TimeSpan.FromSeconds(10));
        var token = await DownloadClientHelpers.GetUTorrentTokenAsync(http, cancellationToken);
        using var response = await http.GetAsync($"gui/?token={Uri.EscapeDataString(token)}&action=add-url&s={Uri.EscapeDataString(request.DownloadUrl)}", cancellationToken);
        response.EnsureSuccessStatusCode();
        return DownloadClientHelpers.GrabSuccess(client, request, "Release URL sent to uTorrent.");
    }

    public override async Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return null;
        using var http = CreateHttp(client, baseUri, TimeSpan.FromSeconds(8));
        var token = await DownloadClientHelpers.GetUTorrentTokenAsync(http, cancellationToken);
        var payload = await http.GetFromJsonAsync<UTorrentListResponse>($"gui/?token={Uri.EscapeDataString(token)}&list=1", cancellationToken);
        var queue = (payload?.Torrents ?? []).Select(item =>
        {
            var hash = item.Count > 0 ? AsString(item[0]) : Guid.CreateVersion7().ToString("N");
            var name = item.Count > 2 ? AsString(item[2]) : "Unknown uTorrent item";
            var size = item.Count > 3 ? AsInt64(item[3]) : 0;
            var progress = item.Count > 4 ? Math.Round(AsDouble(item[4]) / 10d, 1) : 0;
            var speed = item.Count > 9 ? AsDouble(item[9]) / 1_000_000d : 0;
            // uTorrent's list array is positional: index 8 is upload rate in
            // bytes per second, index 9 is download.
            var uploadSpeed = item.Count > 8 ? AsDouble(item[8]) / 1_000_000d : 0;
            var category = item.Count > 11 ? AsString(item[11]) : string.Empty;
            return new DownloadQueueItem(hash, client.Id, client.Name, client.Protocol, DownloadClientHelpers.InferMediaType(client, category), DownloadClientHelpers.CleanReleaseTitle(name), name,
                category, progress >= 100 ? DownloadQueueStatuses.ImportReady : speed > 0 ? DownloadQueueStatuses.Downloading : DownloadQueueStatuses.Queued,
                Math.Clamp(progress, 0, 100), Math.Round(speed, 1), item.Count > 10 ? Math.Max(0, Convert.ToInt32(AsDouble(item[10]))) : 0, size,
                (long)(size * (progress / 100d)), item.Count > 12 ? Convert.ToInt32(AsDouble(item[12])) : 0, "uTorrent", null,
                item.Count > 23 ? DownloadClientHelpers.FromUnix(Convert.ToInt64(AsDouble(item[23]))) : capturedUtc,
                UploadSpeedMbps: Math.Round(uploadSpeed, 1));
        }).ToArray();
        return CreateSnapshot(client, queue, capturedUtc, "healthy", $"Connected to uTorrent at {baseUri.Host}:{baseUri.Port}.");
    }

    public override async Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
    {
        // "removedata" is uTorrent's remove-the-file-too verb; "remove" forgets
        // the torrent and leaves the payload where it is (#287).
        var verb = action switch { "pause" => "pause", "resume" => "start", "delete" => "remove", "delete-with-data" => "removedata", "recheck" => "recheck", _ => null };
        if (verb is null) return DownloadClientHelpers.Unsupported(client, queueItemId, action, "uTorrent");
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.MissingAddress(client, queueItemId, action);
        using var http = CreateHttp(client, baseUri, TimeSpan.FromSeconds(8));
        var token = await DownloadClientHelpers.GetUTorrentTokenAsync(http, cancellationToken);
        using var response = await http.GetAsync($"gui/?token={Uri.EscapeDataString(token)}&action={verb}&hash={Uri.EscapeDataString(queueItemId)}", cancellationToken);
        return new(client.Id, queueItemId, action, response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "uTorrent action sent." : $"uTorrent returned {(int)response.StatusCode}.");
    }

    public override string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null) => DownloadClientHelpers.NormalizeTextStatus(nativeStatus, progress);

    private static HttpClient CreateHttp(DownloadClientItem client, Uri baseUri, TimeSpan timeout)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), Credentials = DownloadClientHelpers.BuildCredential(client) };
        return new HttpClient(handler, disposeHandler: true) { BaseAddress = baseUri, Timeout = timeout };
    }

    private static string AsString(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    private static double AsDouble(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed) ? parsed : double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
    private static long AsInt64(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed) ? parsed : long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
    private sealed record UTorrentListResponse([property: JsonPropertyName("torrents")] IReadOnlyList<IReadOnlyList<JsonElement>>? Torrents);
}
