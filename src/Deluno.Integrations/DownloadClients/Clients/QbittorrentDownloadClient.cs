using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Connections.Contracts;

namespace Deluno.Integrations.DownloadClients.Clients;

/// <summary>
/// qBittorrent's Web API.
///
/// <para><b>The handler is injectable so this can be tested.</b> It was not,
/// and every other client here is: SABnzbd takes an
/// <see cref="IHttpClientFactory"/> and has tests, this one built its own
/// <see cref="HttpClientHandler"/> inline and had none. That is why a grab
/// could report a release as sent while qBittorrent had quietly added nothing,
/// and why no test could have caught it. The default is exactly what it built
/// before, so a real deployment is unchanged.</para>
/// </summary>
public sealed class QbittorrentDownloadClient(Func<HttpMessageHandler>? handlerFactory = null) : DownloadClientBase
{
    private readonly Func<HttpMessageHandler> handlerFactory =
        handlerFactory ?? (() => new HttpClientHandler { CookieContainer = new CookieContainer() });

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

        using var handler = this.handlerFactory();
        using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(10) };
        await LoginAsync(http, client, cancellationToken);
        using var body = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("urls", request.DownloadUrl),
            new KeyValuePair<string, string>("category", DownloadClientHelpers.ResolveCategory(client, request))
        ]);
        // What was there before, so the answer below can be about what actually
        // happened rather than about what was asked.
        var before = await ListHashesAsync(http, cancellationToken);

        using var response = await http.PostAsync("api/v2/torrents/add", body, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();

        // qBittorrent answers 200 with a body of "Ok." or "Fails.", so the
        // status code on its own says nothing about whether it took the
        // release.
        if (payload.StartsWith("Fails", StringComparison.OrdinalIgnoreCase))
        {
            return DownloadClientHelpers.GrabFailure(client, request, "qBittorrent refused the release URL.") with
            {
                ResponseCode = (int)response.StatusCode,
                ResponseJson = payload
            };
        }

        // And 200 "Ok." does not mean it added anything. A torrent whose
        // infohash it already holds is not an error to qBittorrent — it simply
        // keeps the one it has and says Ok. Deluno used to report that as a
        // clean send and then wait for a download that was never going to
        // start, which is exactly what it did on the lab: "Release URL sent to
        // qBittorrent" against a torrent stuck in missingFiles from a previous
        // run.
        // Adding by URL is asynchronous: qBittorrent answers "Ok." as soon as it
        // has accepted the job, then fetches the .torrent and only afterwards
        // does the infohash appear in its list. Asking once, immediately, is
        // asking before the answer exists.
        //
        // That is what this used to do, so *every* grab of a genuinely new
        // torrent was reported as "it already holds this release". On the lab
        // rig qBittorrent held nothing, Deluno grabbed, recorded the dispatch
        // failed, and qBittorrent then held the torrent — the release had
        // arrived and the film stayed Missing, with a blocker card explaining a
        // duplicate that never existed.
        var after = await WaitForNewHashAsync(http, before, cancellationToken);
        if (before is not null && after is not null && !after.Except(before).Any())
        {
            return DownloadClientHelpers.GrabFailure(
                client,
                request,
                "qBittorrent accepted the request but added no torrent, which means it already holds this release. Remove it there, or force a re-download, before expecting this to start.") with
            {
                ResponseCode = (int)response.StatusCode,
                ResponseJson = payload
            };
        }

        // Record the infohash the client just took.
        //
        // Waiting for it above is what makes this possible at all, and without
        // it the dispatch carried no queue item id: nothing downstream could
        // tie the release to the torrent. That is why a stuck download could
        // not be followed, and why forcing a re-download could not work out
        // which item to ask the client to forget.
        var added = before is not null && after is not null
            ? after.Except(before).FirstOrDefault()
            : null;

        return DownloadClientHelpers.GrabSuccess(client, request, "Release URL sent to qBittorrent.") with
        {
            ExternalId = added
        };
    }

    public override async Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return CreateConfigurationSnapshot(client, capturedUtc, "Download client address is missing.");

        using var handler = this.handlerFactory();
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
        using var handler = this.handlerFactory();
        using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(8) };
        await LoginAsync(http, client, cancellationToken);
        // One arm per verb, and `deleteFiles` set by the arm. The pause and
        // resume pairs are qBittorrent's own rename across versions, tried in
        // order; everything else is one endpoint.
        //
        // `forget` is the same request as `delete-with-data`, and saying so
        // here is the point: qBittorrent refuses a release because it still
        // holds the infohash, so once the torrent is gone it accepts the
        // release again and there is nothing left to clear. SABnzbd and NZBGet
        // keep a history that outlives the transfer, and there `forget` is a
        // second request — the two are not the same verb by accident.
        var (endpoints, deleteFiles) = action switch
        {
            DownloadClientActions.Pause => (new[] { "api/v2/torrents/stop", "api/v2/torrents/pause" }, (bool?)null),
            DownloadClientActions.Resume => (new[] { "api/v2/torrents/start", "api/v2/torrents/resume" }, null),
            DownloadClientActions.Recheck => (new[] { "api/v2/torrents/recheck" }, null),
            DownloadClientActions.Delete => (new[] { "api/v2/torrents/delete" }, false),
            DownloadClientActions.DeleteWithData => (new[] { "api/v2/torrents/delete" }, true),
            DownloadClientActions.Forget => (new[] { "api/v2/torrents/delete" }, true),
            _ => (null, null)
        };
        if (endpoints is null) return DownloadClientHelpers.Unsupported(client, queueItemId, action, "qBittorrent");
        var pairs = new List<KeyValuePair<string, string>> { new("hashes", queueItemId) };
        if (deleteFiles is { } wipe)
        {
            pairs.Add(new("deleteFiles", wipe ? "true" : "false"));
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

    /// <summary>
    /// The infohashes qBittorrent is holding, or null when it will not say.
    ///
    /// <para>Null rather than empty on purpose: "I could not read the list" and
    /// "the list is empty" lead to opposite conclusions about whether an add
    /// worked, and a grab must not be failed on the strength of a question that
    /// went unanswered.</para>
    /// </summary>
    /// <summary>
    /// The client's hashes once a new one has appeared, or once we have waited
    /// long enough to say it is not going to.
    ///
    /// <para>Bounded deliberately. A grab that really is a duplicate has to
    /// come back and say so rather than hanging, and the caller is a person
    /// waiting on a page — so this spends a few seconds at most, and returns
    /// the moment something new shows up.</para>
    /// </summary>
    private static async Task<HashSet<string>?> WaitForNewHashAsync(
        HttpClient http,
        HashSet<string>? before,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(6);
        HashSet<string>? after = null;

        while (true)
        {
            after = await ListHashesAsync(http, cancellationToken);

            if (before is null || after is null || after.Except(before).Any())
            {
                return after;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return after;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }
    }

    private static async Task<HashSet<string>?> ListHashesAsync(HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            var torrents = await http.GetFromJsonAsync<QbitTorrentItem[]>("api/v2/torrents/info", cancellationToken);
            return torrents is null
                ? null
                : torrents
                    .Select(torrent => torrent.Hash)
                    .Where(hash => !string.IsNullOrWhiteSpace(hash))
                    .Select(hash => hash!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
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
