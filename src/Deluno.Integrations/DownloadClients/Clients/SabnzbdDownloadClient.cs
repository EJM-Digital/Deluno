using System.Net;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Connections.Contracts;

namespace Deluno.Integrations.DownloadClients.Clients;

public sealed class SabnzbdDownloadClient(IHttpClientFactory httpClientFactory) : DownloadClientBase
{
    public override string Protocol => "sabnzbd";

    public override DownloadClientTelemetryCapabilities Capabilities { get; } = new(
        SupportsQueue: true, SupportsHistory: true, SupportsPauseResume: true, SupportsRemove: true,
        SupportsRecheck: false, SupportsImportPath: true, AuthMode: "api-key");

    public override async Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.GrabFailure(client, request, "Download client address is missing.");
        var apiKey = client.Secret ?? client.Username;
        if (string.IsNullOrWhiteSpace(apiKey)) return DownloadClientHelpers.GrabFailure(client, request, "SABnzbd API key is missing.");
        var uri = new Uri(baseUri, $"api?{DownloadClientHelpers.BuildQuery(new Dictionary<string, string>
        {
            ["mode"] = "addurl", ["apikey"] = apiKey, ["name"] = request.DownloadUrl,
            ["cat"] = DownloadClientHelpers.ResolveCategory(client, request), ["output"] = "json"
        })}");
        using var response = await httpClientFactory.CreateClient("download-clients").GetAsync(uri, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        string? externalId = null;
        var accepted = false;
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            accepted = document.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.True;
            if (document.RootElement.TryGetProperty("nzo_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            {
                externalId = ids.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
            }
        }
        catch (JsonException)
        {
            // The grab service retains the raw payload for the typed failure.
        }

        if (!accepted)
        {
            return DownloadClientHelpers.GrabFailure(client, request, "SABnzbd did not accept the release URL.") with
            {
                ResponseCode = (int)response.StatusCode,
                ResponseJson = responseJson
            };
        }

        return DownloadClientHelpers.GrabSuccess(client, request, "Release URL sent to SABnzbd.") with
        {
            ResponseCode = (int)response.StatusCode,
            ResponseJson = responseJson,
            ExternalId = externalId
        };
    }

    public override async Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return CreateConfigurationSnapshot(client, capturedUtc, "Download client address is missing.");
        var apiKey = client.Secret;
        if (string.IsNullOrWhiteSpace(apiKey)) return CreateConfigurationSnapshot(client, capturedUtc, "SABnzbd API key is missing.");
        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);
        using var response = await http.GetAsync(new Uri(baseUri, $"api?mode=queue&output=json&apikey={Uri.EscapeDataString(apiKey)}"), cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<SabQueueResponse>(cancellationToken);
        var queue = (payload?.Queue?.Slots ?? []).Select(item =>
        {
            var sizeBytes = ParseSize(item.Mb) * 1_000_000L;
            var remainingBytes = ParseSize(item.Mbleft) * 1_000_000L;
            var downloadedBytes = Math.Max(0, sizeBytes - remainingBytes);
            var progress = sizeBytes <= 0 ? 0 : Math.Round(downloadedBytes / (double)sizeBytes * 100, 1);
            return new DownloadQueueItem(item.NzoId ?? item.Filename ?? Guid.CreateVersion7().ToString("N"), client.Id, client.Name, client.Protocol,
                DownloadClientHelpers.InferMediaType(client, item.Category), DownloadClientHelpers.CleanReleaseTitle(item.Filename ?? "Unknown SABnzbd item"),
                item.Filename ?? "Unknown SABnzbd item", item.Category ?? string.Empty, NormalizeStatus(item.Status, progress), progress,
                Math.Round((double)ParseSize(payload?.Queue?.Speed), 1), ParseEta(item.TimeLeft), sizeBytes, downloadedBytes, 0, "SABnzbd",
                QueueError(item.Status), capturedUtc);
        }).ToArray();
        var history = await GetHistoryCoreAsync(http, client, baseUri, apiKey, capturedUtc, cancellationToken);
        return CreateSnapshot(client, queue, capturedUtc, "healthy", $"Connected to SABnzbd at {baseUri.Host}:{baseUri.Port}.", history.Count > 0 ? history : null);
    }

    public override async Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null || string.IsNullOrWhiteSpace(client.Secret)) return [];
        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);
        return await GetHistoryCoreAsync(http, client, baseUri, client.Secret, capturedUtc, cancellationToken);
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
        var apiKey = client.Secret ?? client.Username;
        if (baseUri is null || string.IsNullOrWhiteSpace(apiKey))
        {
            return new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Configuration,
                "Add the SABnzbd address and API key before checking a category.", Supported: true, Found: false);
        }

        try
        {
            var http = httpClientFactory.CreateClient("download-clients");
            http.Timeout = TimeSpan.FromSeconds(8);
            // get_config rather than get_cats: get_cats returns names only, and
            // a name existing is not what makes a download land where Deluno is
            // watching. This one carries each category's folder.
            using var response = await http.GetAsync(
                new Uri(baseUri, $"api?mode=get_config&section=categories&output=json&apikey={Uri.EscapeDataString(apiKey)}"),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Unreachable,
                    $"SABnzbd returned {(int)response.StatusCode} while checking its categories.", Supported: true, Found: false);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var entry = ReadCategoryEntries(document.RootElement)
                .FirstOrDefault(item => string.Equals(item.Name, normalizedCategory, StringComparison.OrdinalIgnoreCase));
            var found = entry.Name is not null;

            if (found)
            {
                return string.IsNullOrWhiteSpace(entry.Directory)
                    ? new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Configuration,
                        $"SABnzbd has a category named {normalizedCategory}, but it has no folder, so downloads will go to SABnzbd's default completed folder rather than one Deluno watches. Set a folder on the category in SABnzbd.",
                        Supported: true, Found: true, SavePath: null)
                    : new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Ready,
                        $"SABnzbd has a category named {normalizedCategory}, saving to {entry.Directory}.",
                        Supported: true, Found: true, SavePath: entry.Directory);
            }
            return new(
                client.Id,
                client.Name,
                normalizedCategory,
                found ? DownloadClientCategoryStatuses.Ready : DownloadClientCategoryStatuses.Missing,
                found
                    ? $"SABnzbd has a category named {normalizedCategory}."
                    : $"SABnzbd does not have a category named {normalizedCategory}. Create it there before using it in Deluno.",
                Supported: true,
                Found: found);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Unreachable,
                $"Deluno could not reach SABnzbd to check its categories: {ex.Message}", Supported: true, Found: false);
        }
    }

    public override async Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.MissingAddress(client, queueItemId, action);
        if (string.IsNullOrWhiteSpace(client.Secret))
        {
            return DownloadClientHelpers.ActionFailure(
                client,
                queueItemId,
                action,
                "SABnzbd API key is missing.",
                upstreamDetail: "The client configuration has no API key.",
                category: "configuration");
        }
        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);

        if (string.Equals(action, DownloadClientActions.Forget, StringComparison.OrdinalIgnoreCase))
        {
            return await ForgetAsync(http, client, baseUri, client.Secret, queueItemId, cancellationToken);
        }

        // One arm per verb, and `del_files` is the whole of the difference
        // between two of them. `delete-with-data` was absent entirely until
        // now, which is how a forced re-download against a usenet client came
        // to do nothing while reporting success: the override asked for it, and
        // SABnzbd answered "does not support this action".
        //
        // `forget` is handled above rather than here, because on SABnzbd it is
        // genuinely a different request and not a flag on this one — its
        // duplicate detection reads the history, which outlives the queue.
        var mode = action switch
        {
            DownloadClientActions.Pause => "queue&name=pause",
            DownloadClientActions.Resume => "queue&name=resume",
            DownloadClientActions.Delete => "queue&name=delete",
            DownloadClientActions.DeleteWithData => "queue&name=delete&del_files=1",
            // SABnzbd verifies its own downloads and exposes no per-item
            // recheck, so that verb is refused rather than quietly mapped onto
            // something adjacent.
            _ => null
        };
        if (mode is null) return DownloadClientHelpers.Unsupported(client, queueItemId, action, "SABnzbd");
        using var response = await http.GetAsync(new Uri(baseUri, $"api?mode={mode}&value={Uri.EscapeDataString(queueItemId)}&apikey={Uri.EscapeDataString(client.Secret)}&output=json"), cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return DownloadClientHelpers.ActionSuccess(client, queueItemId, action, "SABnzbd action sent.");
        }

        return DownloadClientHelpers.ActionFailure(
            client,
            queueItemId,
            action,
            $"SABnzbd returned {(int)response.StatusCode}.",
            response.StatusCode);
    }

    /// <summary>
    /// Remove this download from SABnzbd and from its memory of having had it.
    ///
    /// <para><b>The history is the half that matters.</b> SABnzbd's duplicate
    /// detection reads its history, not its queue, so emptying the queue leaves
    /// the record that refuses the same release next time. A force that only
    /// cleared the queue would report success and change nothing — which is a
    /// worse failure than doing nothing, because it is a confident one.</para>
    ///
    /// <para>Both calls are made because the item is in one place or the other
    /// and Deluno does not know which: a download interrupted half way is in
    /// the queue, and one that completed and was then deleted from disk is in
    /// the history. Neither call failing to find it is an error.</para>
    /// </summary>
    private static async Task<DownloadClientActionResult> ForgetAsync(
        HttpClient http,
        DownloadClientItem client,
        Uri baseUri,
        string apiKey,
        string queueItemId,
        CancellationToken cancellationToken)
    {
        // SABnzbd's history delete takes an nzo id — and also the words "all"
        // and "failed", which mean "empty the history". Deluno only ever passes
        // an id it read from its own dispatch record, so this cannot happen
        // today; it is here because the cost of being wrong once is somebody's
        // entire download history, and the check is one comparison.
        if (DangerousHistorySelectors.Contains(queueItemId))
        {
            return DownloadClientHelpers.ActionFailure(
                client,
                queueItemId,
                DownloadClientActions.Forget,
                $"\"{queueItemId}\" is not a download id — to SABnzbd it means the whole history, so Deluno will not send it.",
                upstreamDetail: "Refused before the request was made.",
                category: "configuration");
        }

        var id = Uri.EscapeDataString(queueItemId);
        var key = Uri.EscapeDataString(apiKey);

        var queueRemoved = await SendAsync(http, baseUri, $"api?mode=queue&name=delete&value={id}&del_files=1&apikey={key}&output=json", cancellationToken);
        var historyRemoved = await SendAsync(http, baseUri, $"api?mode=history&name=delete&value={id}&del_files=1&apikey={key}&output=json", cancellationToken);

        if (historyRemoved.Reached || queueRemoved.Reached)
        {
            var what = (queueRemoved.Accepted, historyRemoved.Accepted) switch
            {
                (true, true) => "Removed the download and its history entry from SABnzbd.",
                (false, true) => "Removed SABnzbd's history entry, so it will accept this release again.",
                (true, false) => "Removed the download from SABnzbd's queue. It had no history entry for it.",
                _ => "SABnzbd had no record of this release to remove."
            };

            return DownloadClientHelpers.ActionSuccess(client, queueItemId, DownloadClientActions.Forget, what);
        }

        return DownloadClientHelpers.ActionFailure(
            client,
            queueItemId,
            DownloadClientActions.Forget,
            "SABnzbd could not be reached to forget this release.",
            historyRemoved.StatusCode ?? queueRemoved.StatusCode);
    }

    /// <summary>
    /// Values SABnzbd reads as "everything" rather than as one download.
    /// </summary>
    private static readonly HashSet<string> DangerousHistorySelectors =
        new(StringComparer.OrdinalIgnoreCase) { "all", "failed", "completed" };

    /// <summary>
    /// Whether SABnzbd answered at all, and whether it said yes.
    ///
    /// <para>It replies <c>200</c> with <c>{"status": false}</c> when it has
    /// nothing matching the id, so the HTTP code alone cannot tell "gone" from
    /// "never there" — and both are fine for a forget.</para>
    /// </summary>
    private static async Task<SabActionOutcome> SendAsync(HttpClient http, Uri baseUri, string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(baseUri, path), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new SabActionOutcome(Reached: true, Accepted: false, response.StatusCode);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var accepted = body.Contains("\"status\": true", StringComparison.OrdinalIgnoreCase)
                           || body.Contains("\"status\":true", StringComparison.OrdinalIgnoreCase);
            return new SabActionOutcome(Reached: true, accepted, response.StatusCode);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            return new SabActionOutcome(Reached: false, Accepted: false, null);
        }
    }

    private readonly record struct SabActionOutcome(bool Reached, bool Accepted, HttpStatusCode? StatusCode);

    public override string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
    {
        var normalized = nativeStatus?.ToLowerInvariant() ?? string.Empty;
        if ((progress ?? 0) >= 99.9 || normalized.Contains("complete")) return DownloadQueueStatuses.ImportReady;
        if (normalized.Contains("pause") || normalized.Contains("queued")) return DownloadQueueStatuses.Queued;
        if (normalized.Contains("fail") || normalized.Contains("error")) return DownloadQueueStatuses.Stalled;
        return DownloadQueueStatuses.Downloading;
    }

    private static async Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryCoreAsync(HttpClient http, DownloadClientItem client, Uri baseUri, string apiKey, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri(baseUri, $"api?mode=history&output=json&apikey={Uri.EscapeDataString(apiKey)}"), cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<SabHistoryResponse>(cancellationToken);
        return (payload?.History?.Slots ?? []).Select(item => new DownloadClientHistoryItem(
            item.NzoId ?? item.Name ?? Guid.CreateVersion7().ToString("N"), client.Id, client.Name, client.Protocol,
            DownloadClientHelpers.InferMediaType(client, item.Category), DownloadClientHelpers.CleanReleaseTitle(item.Name ?? "Unknown SABnzbd history item"),
            item.Name ?? "Unknown SABnzbd history item", item.Category ?? string.Empty, NormalizeHistoryOutcome(item.Status ?? string.Empty), "SABnzbd",
            ParseHistorySize(item.Bytes), item.Completed is > 0 ? DateTimeOffset.FromUnixTimeSeconds(item.Completed.Value) : capturedUtc,
            string.IsNullOrWhiteSpace(item.FailMessage) ? QueueError(item.Status) : item.FailMessage, item.Storage,
            HistorySource: "native",
            ExternalId: item.NzoId)).ToArray();
    }

    private static long ParseSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var cleaned = value.Trim().Replace("/s", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "", StringComparison.OrdinalIgnoreCase);
        var multiplier = cleaned.EndsWith("GB", StringComparison.OrdinalIgnoreCase) || cleaned.EndsWith("G", StringComparison.OrdinalIgnoreCase) ? 1000d
            : cleaned.EndsWith("KB", StringComparison.OrdinalIgnoreCase) || cleaned.EndsWith("K", StringComparison.OrdinalIgnoreCase) ? .001d
            : cleaned.EndsWith("B", StringComparison.OrdinalIgnoreCase) && !cleaned.EndsWith("MB", StringComparison.OrdinalIgnoreCase) && !cleaned.EndsWith("GB", StringComparison.OrdinalIgnoreCase) && !cleaned.EndsWith("KB", StringComparison.OrdinalIgnoreCase) ? .000001d : 1d;
        cleaned = cleaned.Replace("GB", "", StringComparison.OrdinalIgnoreCase).Replace("MB", "", StringComparison.OrdinalIgnoreCase).Replace("KB", "", StringComparison.OrdinalIgnoreCase).Replace("G", "", StringComparison.OrdinalIgnoreCase).Replace("M", "", StringComparison.OrdinalIgnoreCase).Replace("K", "", StringComparison.OrdinalIgnoreCase).Replace("B", "", StringComparison.OrdinalIgnoreCase);
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? Convert.ToInt64(parsed * multiplier) : 0;
    }

    private static int ParseEta(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var parts = value.Split(':').Select(part => int.TryParse(part, out var parsed) ? parsed : 0).ToArray();
        return parts.Length switch { 3 => parts[0] * 3600 + parts[1] * 60 + parts[2], 2 => parts[0] * 60 + parts[1], _ => 0 };
    }

    private static long ParseHistorySize(JsonElement? value) => value is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out var parsed) ? parsed : value is not null && long.TryParse(value.Value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
    private static string? QueueError(string? status) => string.IsNullOrWhiteSpace(status) ? null : status.Contains("fail", StringComparison.OrdinalIgnoreCase) || status.Contains("error", StringComparison.OrdinalIgnoreCase) || status.Contains("stall", StringComparison.OrdinalIgnoreCase) ? status : null;
    private static string NormalizeHistoryOutcome(string value) => value.Equals("completed", StringComparison.OrdinalIgnoreCase) || value.Equals("succeeded", StringComparison.OrdinalIgnoreCase) || value.Equals("success", StringComparison.OrdinalIgnoreCase) ? DownloadQueueStatuses.Completed : value.Contains("fail", StringComparison.OrdinalIgnoreCase) || value.Contains("error", StringComparison.OrdinalIgnoreCase) ? "failed" : value.Contains("import", StringComparison.OrdinalIgnoreCase) ? DownloadQueueStatuses.ImportReady : value.Length == 0 ? "unknown" : value.Trim().ToLowerInvariant();

    /// <summary>
    /// Each configured category with the folder it writes to, from
    /// <c>mode=get_config&amp;section=categories</c>.
    /// </summary>
    private static IEnumerable<(string? Name, string? Directory)> ReadCategoryEntries(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("config", out var config) ||
            !config.TryGetProperty("categories", out var categories) ||
            categories.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var category in categories.EnumerateArray())
        {
            if (category.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = category.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                ? nameValue.GetString()
                : null;
            var directory = category.TryGetProperty("dir", out var dirValue) && dirValue.ValueKind == JsonValueKind.String
                ? dirValue.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(name))
            {
                yield return (name, directory);
            }
        }
    }

    private static IReadOnlyList<string> ReadCategoryNames(JsonElement root)
    {
        if (!root.TryGetProperty("categories", out var categories))
        {
            return [];
        }

        return categories.ValueKind switch
        {
            JsonValueKind.Array => categories.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray(),
            JsonValueKind.Object => categories.EnumerateObject().Select(item => item.Name).ToArray(),
            _ => []
        };
    }

    private sealed record SabQueueResponse([property: JsonPropertyName("queue")] SabQueue? Queue);
    private sealed record SabQueue([property: JsonPropertyName("speed")] string? Speed, [property: JsonPropertyName("slots")] IReadOnlyList<SabSlot>? Slots);
    private sealed record SabSlot([property: JsonPropertyName("nzo_id")] string? NzoId, [property: JsonPropertyName("filename")] string? Filename, [property: JsonPropertyName("cat")] string? Category, [property: JsonPropertyName("status")] string? Status, [property: JsonPropertyName("mb")] string? Mb, [property: JsonPropertyName("mbleft")] string? Mbleft, [property: JsonPropertyName("timeleft")] string? TimeLeft);
    private sealed record SabHistoryResponse([property: JsonPropertyName("history")] SabHistory? History);
    private sealed record SabHistory([property: JsonPropertyName("slots")] IReadOnlyList<SabHistorySlot>? Slots);
    private sealed record SabHistorySlot([property: JsonPropertyName("nzo_id")] string? NzoId, [property: JsonPropertyName("name")] string? Name, [property: JsonPropertyName("category")] string? Category, [property: JsonPropertyName("status")] string? Status, [property: JsonPropertyName("bytes")] JsonElement? Bytes, [property: JsonPropertyName("completed")] long? Completed, [property: JsonPropertyName("fail_message")] string? FailMessage, [property: JsonPropertyName("storage")] string? Storage);
}
