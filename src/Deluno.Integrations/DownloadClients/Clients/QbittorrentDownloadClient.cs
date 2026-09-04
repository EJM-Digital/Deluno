using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Connections.Contracts;

namespace Deluno.Integrations.DownloadClients.Clients;

public sealed class QbittorrentDownloadClient : DownloadClientBase
{
    public override string Protocol => "qbittorrent";

    public override DownloadClientTelemetryCapabilities Capabilities { get; } = new(
        SupportsQueue: true,
        SupportsHistory: false,
        SupportsPauseResume: true,
        SupportsRemove: true,
        SupportsRecheck: true,
        SupportsImportPath: true,
        AuthMode: "form");

    public override async Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.GrabFailure(client, request, "Download client address is missing.");

        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(10) };
        await LoginAsync(http, client, cancellationToken);
        using var body = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("urls", request.DownloadUrl),
            new KeyValuePair<string, string>("category", DownloadClientHelpers.ResolveCategory(client, request))
        ]);
        using var response = await http.PostAsync("api/v2/torrents/add", body, cancellationToken);
        response.EnsureSuccessStatusCode();
        return DownloadClientHelpers.GrabSuccess(client, request, "Release URL sent to qBittorrent.");
    }

    public override async Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return CreateConfigurationSnapshot(client, capturedUtc, "Download client address is missing.");

        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(8) };
        await LoginAsync(http, client, cancellationToken);
        var torrents = await http.GetFromJsonAsync<IReadOnlyList<QbitTorrentItem>>("api/v2/torrents/info", cancellationToken) ?? [];
        var queue = torrents.Select(item => new DownloadQueueItem(
            item.Hash ?? item.Name ?? Guid.CreateVersion7().ToString("N"), client.Id, client.Name, client.Protocol,
            DownloadClientHelpers.InferMediaType(client, item.Category), DownloadClientHelpers.CleanReleaseTitle(item.Name ?? "Unknown qBittorrent item"),
            item.Name ?? "Unknown qBittorrent item", item.Category ?? string.Empty, NormalizeStatus(item.State, item.Progress),
            Math.Clamp(Math.Round((item.Progress ?? 0) * 100, 1), 0, 100), Math.Round((item.DownloadSpeed ?? 0) / 1_000_000d, 1),
            Convert.ToInt32(Math.Clamp(item.Eta ?? 0, 0, int.MaxValue)), item.Size ?? 0, item.Downloaded ?? 0, item.NumSeeds ?? 0,
            "qBittorrent", item.State?.Contains("error", StringComparison.OrdinalIgnoreCase) == true ? item.State : null,
            DownloadClientHelpers.FromUnix(item.AddedOn), DownloadClientHelpers.ChoosePath(item.ContentPath, item.SavePath),
            LibraryId: null,
            HealthFindings: null,
            // Both come back on the same torrents/info call, so a sharing rule
            // costs no extra request. seeding_time is seconds since the torrent
            // completed; it is 0 while still downloading.
            Ratio: item.Ratio,
            SeedingMinutes: item.SeedingTimeSeconds is null ? null : (int)Math.Clamp(item.SeedingTimeSeconds.Value / 60, 0, int.MaxValue),
            UploadSpeedMbps: Math.Round((item.UploadSpeed ?? 0) / 1_000_000d, 1))).ToArray();
        return CreateSnapshot(client, queue, capturedUtc, "healthy", $"Connected to qBittorrent at {baseUri.Host}:{baseUri.Port}.");
    }

    public override async Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(
        DownloadClientItem client,
        string category,
        CancellationToken cancellationToken)
    {
        var normalizedCategory = category.Trim();
        if (string.IsNullOrWhiteSpace(normalizedCategory))
        {
            return new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Configuration,
                "Enter a category before checking it.", Supported: true, Found: false);
        }

        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null)
        {
            return new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Configuration,
                "Add the qBittorrent address before checking a category.", Supported: true, Found: false);
        }

        try
        {
            using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
            using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(8) };
            await LoginAsync(http, client, cancellationToken);
            using var response = await http.GetAsync("api/v2/torrents/categories", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Unsupported,
                    "This qBittorrent version does not expose its category list. You can still use the category, but Deluno cannot verify it.", Supported: false, Found: false);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Unreachable,
                    $"qBittorrent returned {(int)response.StatusCode} while checking its categories.", Supported: true, Found: false);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var match = document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject()
                    .Where(item => string.Equals(item.Name, normalizedCategory, StringComparison.OrdinalIgnoreCase))
                    .Select(item => (JsonElement?)item.Value)
                    .FirstOrDefault()
                : null;

            if (match is null)
            {
                return new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Missing,
                    $"qBittorrent does not have a category named {normalizedCategory}. Create it there before using it in Deluno.",
                    Supported: true, Found: false);
            }

            // The name existing was never the interesting part. A category with
            // no save path sends its downloads somewhere qBittorrent picks -
            // with Automatic Torrent Management on that is
            // <global save path>\<category name> - and Deluno used to call that
            // "ready" while nothing ever arrived where it was watching.
            var savePath = match.Value.ValueKind == JsonValueKind.Object &&
                           match.Value.TryGetProperty("savePath", out var savePathValue) &&
                           savePathValue.ValueKind == JsonValueKind.String
                ? savePathValue.GetString()
                : null;

            return string.IsNullOrWhiteSpace(savePath)
                ? new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Configuration,
                    $"qBittorrent has a category named {normalizedCategory}, but it has no save path, so downloads will go wherever qBittorrent defaults to rather than a folder Deluno watches. Set a save path on the category in qBittorrent.",
                    Supported: true, Found: true, SavePath: null)
                : new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Ready,
                    $"qBittorrent has a category named {normalizedCategory}, saving to {savePath}.",
                    Supported: true, Found: true, SavePath: savePath);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Unreachable,
                $"Deluno could not reach qBittorrent to check its categories: {ex.Message}", Supported: true, Found: false);
        }
    }

    public override async Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.MissingAddress(client, queueItemId, action);
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(8) };
        await LoginAsync(http, client, cancellationToken);
        var endpoints = action switch
        {
            "pause" => new[] { "api/v2/torrents/stop", "api/v2/torrents/pause" },
            "resume" => new[] { "api/v2/torrents/start", "api/v2/torrents/resume" },
            "delete" => new[] { "api/v2/torrents/delete" },
            "delete-with-data" => new[] { "api/v2/torrents/delete" },
            "recheck" => new[] { "api/v2/torrents/recheck" },
            _ => null
        };
        if (endpoints is null) return DownloadClientHelpers.Unsupported(client, queueItemId, action, "qBittorrent");
        var pairs = new List<KeyValuePair<string, string>> { new("hashes", queueItemId) };
        if (action is "delete" or "delete-with-data")
        {
            pairs.Add(new("deleteFiles", action == "delete-with-data" ? "true" : "false"));
        }
        HttpResponseMessage? lastResponse = null;
        foreach (var endpoint in endpoints)
        {
            lastResponse?.Dispose();
            lastResponse = await http.PostAsync(endpoint, new FormUrlEncodedContent(pairs), cancellationToken);
            if (lastResponse.IsSuccessStatusCode || lastResponse.StatusCode != HttpStatusCode.NotFound) break;
        }
        using (lastResponse)
        {
            return lastResponse?.IsSuccessStatusCode == true
                ? DownloadClientHelpers.ActionSuccess(client, queueItemId, action, "qBittorrent action sent.")
                : DownloadClientHelpers.ActionFailure(
                    client,
                    queueItemId,
                    action,
                    $"qBittorrent returned {(int?)lastResponse?.StatusCode ?? 0}.",
                    lastResponse?.StatusCode);
        }
    }

    public override string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
    {
        var normalized = nativeStatus?.ToLowerInvariant() ?? string.Empty;
        if ((progress ?? 0) >= 1 || normalized.Contains("upload")) return DownloadQueueStatuses.ImportReady;
        if (normalized.Contains("pause") || normalized.Contains("queued")) return DownloadQueueStatuses.Queued;
        if (normalized.Contains("error") || normalized.Contains("stalled")) return DownloadQueueStatuses.Stalled;
        return DownloadQueueStatuses.Downloading;
    }

    private static async Task LoginAsync(HttpClient http, DownloadClientItem client, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(client.Username) && string.IsNullOrWhiteSpace(client.Secret)) return;
        using var body = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("username", client.Username ?? string.Empty),
            new KeyValuePair<string, string>("password", client.Secret ?? string.Empty)
        ]);
        using var response = await http.PostAsync("api/v2/auth/login", body, cancellationToken);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!text.Contains("Ok.", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("qBittorrent rejected the configured username/password.");
    }

    private sealed record QbitTorrentItem(
        [property: JsonPropertyName("hash")] string? Hash,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("category")] string? Category,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("progress")] double? Progress,
        [property: JsonPropertyName("dlspeed")] long? DownloadSpeed,
        [property: JsonPropertyName("upspeed")] long? UploadSpeed,
        [property: JsonPropertyName("eta")] long? Eta,
        [property: JsonPropertyName("size")] long? Size,
        [property: JsonPropertyName("downloaded")] long? Downloaded,
        [property: JsonPropertyName("num_seeds")] int? NumSeeds,
        [property: JsonPropertyName("added_on")] long? AddedOn,
        [property: JsonPropertyName("save_path")] string? SavePath,
        [property: JsonPropertyName("content_path")] string? ContentPath,
        [property: JsonPropertyName("ratio")] double? Ratio = null,
        [property: JsonPropertyName("seeding_time")] long? SeedingTimeSeconds = null);
}
