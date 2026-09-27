using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Connections.Contracts;
using Deluno.Contracts;

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
public sealed class QbittorrentDownloadClient(Func<HttpMessageHandler>? handlerFactory = null) : DownloadClientBase, IQbittorrentBlocklistSyncClient
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
        AuthMode: "form",
        SupportsQueueReorder: true,
        SupportsForceStart: true);

    public override async Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.GrabFailure(client, request, "Download client address is missing.");

        using var handler = this.handlerFactory();
        using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(10) };
        await AuthenticateAsync(http, client, cancellationToken);
        // #811: the rest of qBittorrent's add-time options, sent only when
        // Deluno's setting for them is not the client's own default (off).
        // Tags reuse #808's routing field rather than a second one -- the same
        // names Deluno already routes by now show up on the torrent itself.
        var addTimePairs = new List<KeyValuePair<string, string>>
        {
            new("skip_checking", client.SkipHashCheck ? "true" : "false"),
            new("sequentialDownload", client.SequentialDownload ? "true" : "false"),
            new("firstLastPiecePrio", client.FirstLastPiecePrio ? "true" : "false")
        };
        if (!string.IsNullOrWhiteSpace(client.Tags))
        {
            addTimePairs.Add(new("tags", client.Tags));
        }
        // #811: savepath, when this client has one configured -- sent at add
        // time same as the others, and reapplied with setLocation once the
        // hash is known below, since qBittorrent's own Automatic Torrent
        // Management can otherwise silently override the add-time value.
        if (!string.IsNullOrWhiteSpace(client.Location))
        {
            addTimePairs.Add(new("savepath", client.Location));
        }

        // A .torrent Deluno fetched with a tracker's sign-in goes up as a file;
        // qBittorrent could not fetch it by URL itself (#606).
        using HttpContent body = request.TorrentFile is { } file
            ? BuildMultipartAddRequest(file, client, request, addTimePairs)
            : new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("urls", request.DownloadUrl),
                new KeyValuePair<string, string>("category", DownloadClientHelpers.ResolveCategory(client, request)),
                new KeyValuePair<string, string>("paused", client.AddPaused ? "true" : "false"),
                .. addTimePairs
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
        //
        // #876: "Fails." is qBittorrent looking at the exact link or file it
        // was handed and refusing it outright, in the same request — a dead
        // link or a torrent it could not parse, not a setup problem. Read as
        // a dead release, so the caller blocklists it and lets the next
        // search pick a different one, instead of leaving it to fail the
        // same way again on its ordinary retry cycle.
        if (payload.StartsWith("Fails", StringComparison.OrdinalIgnoreCase))
        {
            return DownloadClientHelpers.DeadReleaseGrabFailure(client, request, "qBittorrent refused the release URL. It may be a dead link or a file it could not read.") with
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
            // No new torrent is also what a link qBittorrent could not read
            // looks like: it logs "Failed to add torrent" and still answered
            // Ok. Only a hash Deluno knows, already in the list, is a duplicate.
            var knownHash = Search.Trackers.TrackerMagnet.InfoHashOf(request.DownloadUrl);
            if (knownHash is { Length: 40 } duplicateHash && before.Contains(duplicateHash, StringComparer.OrdinalIgnoreCase))
            {
                // #877: "the download client says it already has this one" is
                // nobody's fault (cleanup sheet B3) — Usenet clients already
                // follow the existing item instead of failing the grab, and a
                // torrent client should too, rather than reporting a failed
                // download and asking the person to remove it themselves.
                // Recording this as a success with the existing torrent's own
                // hash as the external id is what lets Deluno follow it: the
                // ordinary queue poll picks the torrent up by that hash next
                // time, under whatever status it is actually in, exactly as if
                // this dispatch had added it.
                return DownloadClientHelpers.GrabSuccess(
                    client,
                    request,
                    "qBittorrent already holds this release. Deluno is following the copy it has instead of sending another.") with
                {
                    ExternalId = duplicateHash,
                    ResponseCode = (int)response.StatusCode,
                    ResponseJson = payload,
                    IsDuplicateFollow = true
                };
            }

            var message = knownHash is { Length: 40 }
                ? "qBittorrent accepted the magnet but added no torrent. Its log says why."
                : "qBittorrent accepted the request but added no torrent. Either it already holds this release (remove it there, or force a re-download), or it could not read what the link returned; its log says which.";
            return DownloadClientHelpers.GrabFailure(
                client,
                request,
                message) with
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

        // #811: setShareLimits, once the hash is known. Best-effort -- a
        // failure here does not fail the grab, since the download itself
        // already succeeded; the client just keeps its own default limits.
        if (added is not null && HasShareLimits(client))
        {
            await TrySetShareLimitsAsync(http, client, added, cancellationToken);
        }

        // #811: setCategory and setLocation, once the hash is known — the
        // same explicit-write path setShareLimits above uses, so a category
        // or save path Deluno resolved is not left to whatever the add-time
        // form field happened to take (qBittorrent's Automatic Torrent
        // Management can override an add-time savepath; the explicit call
        // does not go through it the same way).
        if (added is not null)
        {
            await TrySetCategoryAsync(http, client, request, added, cancellationToken);
            if (!string.IsNullOrWhiteSpace(client.Location))
            {
                await TrySetLocationAsync(http, client, added, cancellationToken);
            }
        }

        return DownloadClientHelpers.GrabSuccess(client, request, "Release URL sent to qBittorrent.") with
        {
            ExternalId = added
        };
    }

    private static bool HasShareLimits(DownloadClientItem client)
        => client.ShareRatioLimit is not null
            || client.ShareSeedingTimeLimitMinutes is not null
            || client.ShareInactiveSeedingTimeLimitMinutes is not null;

    /// <summary>
    /// #811: qBittorrent's setCategory, applied to the torrent this grab just
    /// added — a best-effort re-assertion of the category already sent at add
    /// time, since a failure here never fails the grab (the category from the
    /// add-time form field is still whatever qBittorrent already took).
    /// </summary>
    private static async Task TrySetCategoryAsync(HttpClient http, DownloadClientItem client, DownloadClientGrabRequest request, string hash, CancellationToken cancellationToken)
    {
        try
        {
            var pairs = new[]
            {
                new KeyValuePair<string, string>("hashes", hash),
                new KeyValuePair<string, string>("category", DownloadClientHelpers.ResolveCategory(client, request))
            };
            using var response = await http.PostAsync("api/v2/torrents/setCategory", new FormUrlEncodedContent(pairs), cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Left alone: the add-time category field already took.
        }
    }

    /// <summary>
    /// #811: qBittorrent's setLocation, applied to the torrent this grab just
    /// added. Automatic Torrent Management can silently override an add-time
    /// <c>savepath</c>, so this client's configured save path is reasserted
    /// explicitly once the hash is known — best-effort, since a failure here
    /// never fails the grab.
    /// </summary>
    private static async Task TrySetLocationAsync(HttpClient http, DownloadClientItem client, string hash, CancellationToken cancellationToken)
    {
        try
        {
            var pairs = new[]
            {
                new KeyValuePair<string, string>("hashes", hash),
                new KeyValuePair<string, string>("location", client.Location!)
            };
            using var response = await http.PostAsync("api/v2/torrents/setLocation", new FormUrlEncodedContent(pairs), cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Left alone: the add-time savepath field already took, or the
            // client keeps whatever location it chose on its own.
        }
    }

    /// <summary>
    /// qBittorrent's setShareLimits, applied to the one torrent this grab just
    /// added. -1 is qBittorrent's own "no limit"; a Deluno field left unset
    /// sends qBittorrent's global default (-2) rather than inventing a limit
    /// nobody configured.
    /// </summary>
    private static async Task TrySetShareLimitsAsync(HttpClient http, DownloadClientItem client, string hash, CancellationToken cancellationToken)
    {
        try
        {
            var pairs = new[]
            {
                new KeyValuePair<string, string>("hashes", hash),
                new KeyValuePair<string, string>("ratioLimit", (client.ShareRatioLimit ?? -2).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("seedingTimeLimit", (client.ShareSeedingTimeLimitMinutes ?? -2).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("inactiveSeedingTimeLimit", (client.ShareInactiveSeedingTimeLimitMinutes ?? -2).ToString(System.Globalization.CultureInfo.InvariantCulture))
            };
            using var response = await http.PostAsync("api/v2/torrents/setShareLimits", new FormUrlEncodedContent(pairs), cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Left alone: the grab already succeeded, and the client keeps
            // seeding under its own default limits until the next successful
            // attempt at this torrent's own settings sets them.
        }
    }

    /// <summary>Builds the multipart add-torrent request for a .torrent file body, with #811's add-time fields alongside the existing category/paused ones.</summary>
    private static MultipartFormDataContent BuildMultipartAddRequest(
        byte[] file,
        DownloadClientItem client,
        DownloadClientGrabRequest request,
        IReadOnlyList<KeyValuePair<string, string>> addTimePairs)
    {
        var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(file), "torrents", "release.torrent" },
            { new StringContent(DownloadClientHelpers.ResolveCategory(client, request)), "category" },
            { new StringContent(client.AddPaused ? "true" : "false"), "paused" }
        };
        foreach (var pair in addTimePairs)
        {
            content.Add(new StringContent(pair.Value), pair.Key);
        }

        return content;
    }

    public override async Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return CreateConfigurationSnapshot(client, capturedUtc, "Download client address is missing.");

        using var handler = this.handlerFactory();
        using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(8) };
        await AuthenticateAsync(http, client, cancellationToken);
        var torrents = await http.GetFromJsonAsync<IReadOnlyList<QbitTorrentItem>>("api/v2/torrents/info", cancellationToken) ?? [];
        var trackerStatusByHash = await GetTrackerStatusesAsync(http, torrents, cancellationToken);
        var queue = torrents.Select(item => new DownloadQueueItem(
            item.Hash ?? item.Name ?? Guid.CreateVersion7().ToString("N"), client.Id, client.Name, client.Protocol,
            DownloadClientHelpers.InferMediaType(client, item.Category), DownloadClientHelpers.CleanReleaseTitle(item.Name ?? "Unknown qBittorrent item"),
            item.Name ?? "Unknown qBittorrent item", item.Category ?? string.Empty, NormalizeStatus(item.State, item.Progress),
            Math.Clamp(Math.Round((item.Progress ?? 0) * 100, 1), 0, 100), Math.Round((item.DownloadSpeed ?? 0) / 1_000_000d, 1),
            // Seeds alone undercounted a swarm with active leechers and no seeds
            // yet, which the no-progress stall gate (#659) reads as "no peers at
            // all" (#660).
            EtaSeconds(item.Eta), item.Size ?? 0, item.Downloaded ?? 0, (item.NumSeeds ?? 0) + (item.NumLeechs ?? 0),
            "qBittorrent", ErrorMessage(item.State),
            DownloadClientHelpers.FromUnix(item.AddedOn), DownloadClientHelpers.ChoosePath(item.ContentPath, item.SavePath),
            LibraryId: null,
            HealthFindings: null,
            // Both come back on the same torrents/info call, so a sharing rule
            // costs no extra request. seeding_time is seconds since the torrent
            // completed; it is 0 while still downloading.
            Ratio: item.Ratio,
            SeedingMinutes: item.SeedingTimeSeconds is null ? null : (int)Math.Clamp(item.SeedingTimeSeconds.Value / 60, 0, int.MaxValue),
            UploadSpeedMbps: Math.Round((item.UploadSpeed ?? 0) / 1_000_000d, 1),
            // #880/#881: qBittorrent only says something specific in the state
            // string itself for missingFiles (the payload is gone from disk)
            // and a bare "error" state (I/O, permissions, disk space). Both
            // are this machine's problem, not the release's — a plain
            // stalled/stalledDL state (no seeds, no error text) is left as a
            // release-side finding.
            IsSetupProblem: IsSetupProblemState(item.State),
            IsPrivate: item.Private,
            TrackerStatus: item.Hash is { Length: > 0 } hash && trackerStatusByHash.TryGetValue(hash, out var trackerStatus)
                ? trackerStatus
                : DownloadTrackerStatuses.Unknown,
            // #811: availability/last_activity/completion_on, where qBittorrent reports them.
            Availability: item.Availability,
            LastActivityUtc: FromUnixOrNull(item.LastActivity),
            CompletionUtc: FromUnixOrNull(item.CompletionOn)))
            .Select(item => DownloadClientHelpers.ReconcileTorrentStatus(item, capturedUtc))
            .ToArray();
        var freeSpaceBytes = await GetFreeSpaceBytesAsync(http, cancellationToken);
        var (health, message) = DownloadClientHelpers.EvaluateDiskSpaceHealth(
            $"Connected to qBittorrent at {baseUri.Host}:{baseUri.Port}.",
            freeSpaceBytes,
            null);
        return CreateSnapshot(client, queue, capturedUtc, health, message, freeSpaceBytes: freeSpaceBytes);
    }

    /// <summary>
    /// <c>server_state.free_space_on_disk</c> from qBittorrent's sync call
    /// (#909) -- a second, cheap request rather than folding this into
    /// <c>torrents/info</c>, which has no such field. A client that will not
    /// answer this at all (an old version, or a momentary hiccup) reports
    /// nothing rather than a false low-space warning.
    /// </summary>
    private static async Task<long?> GetFreeSpaceBytesAsync(HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            var mainData = await http.GetFromJsonAsync<QbitMainData>("api/v2/sync/maindata", cancellationToken);
            return mainData?.ServerState?.FreeSpaceOnDisk;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
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
            await AuthenticateAsync(http, client, cancellationToken);
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

            // A relative save path sits inside qBittorrent's default one: the
            // full folder is what has to match the library's (#616).
            if (!string.IsNullOrWhiteSpace(savePath) && !ClientPaths.IsFullClientPath(savePath))
            {
                using var defaultResponse = await http.GetAsync("api/v2/app/defaultSavePath", cancellationToken);
                var defaultSavePath = defaultResponse.IsSuccessStatusCode ? (await defaultResponse.Content.ReadAsStringAsync(cancellationToken)).Trim() : null;
                savePath = ClientPaths.ClientFolder(defaultSavePath, savePath) ?? savePath;
            }

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

    /// <summary>
    /// qBittorrent's <c>api/v2/torrents/files?hash=</c> (#816): every file
    /// inside one torrent, by its own relative name. Null (not one word about
    /// it) when the address is missing or the call itself fails, so a content
    /// blocklist check falls back to the release name rather than reading
    /// silence as "no files".
    /// </summary>
    public override async Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null || string.IsNullOrWhiteSpace(queueItemId)) return null;

        try
        {
            using var handler = this.handlerFactory();
            using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(8) };
            await LoginAsync(http, client, cancellationToken);
            var query = $"api/v2/torrents/files?hash={Uri.EscapeDataString(queueItemId)}";
            var files = await http.GetFromJsonAsync<QbitTorrentFile[]>(query, cancellationToken);
            return files is null
                ? null
                : files
                    .Select(file => file.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .ToArray();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// #822: qBittorrent's own <c>app/setPreferences</c>, writing only
    /// <c>excluded_file_names</c> - it never touches
    /// <c>excluded_file_names_enabled</c>, so a person's own choice about
    /// whether qBittorrent applies the list is never overridden.
    /// </summary>
    public async Task<(bool Succeeded, string? Message)> PushExcludedFileNamesAsync(
        DownloadClientItem client,
        IReadOnlyList<string> patterns,
        CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return (false, "Download client address is missing.");

        try
        {
            using var handler = this.handlerFactory();
            using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(8) };
            await LoginAsync(http, client, cancellationToken);
            var json = JsonSerializer.Serialize(new { excluded_file_names = string.Join('\n', patterns) });
            using var body = new FormUrlEncodedContent([new KeyValuePair<string, string>("json", json)]);
            using var response = await http.PostAsync("api/v2/app/setPreferences", body, cancellationToken);
            return response.IsSuccessStatusCode
                ? (true, (string?)null)
                : (false, $"qBittorrent returned {(int)response.StatusCode}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return (false, exception.Message);
        }
    }

    public override async Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.MissingAddress(client, queueItemId, action);
        using var handler = this.handlerFactory();
        using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(8) };
        await AuthenticateAsync(http, client, cancellationToken);
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
            // #810: queue priority/reorder and force-start.
            DownloadClientActions.MoveToTop => (new[] { "api/v2/torrents/topPrio" }, null),
            DownloadClientActions.MoveUp => (new[] { "api/v2/torrents/increasePrio" }, null),
            DownloadClientActions.MoveDown => (new[] { "api/v2/torrents/decreasePrio" }, null),
            DownloadClientActions.MoveToBottom => (new[] { "api/v2/torrents/bottomPrio" }, null),
            DownloadClientActions.ForceStart => (new[] { "api/v2/torrents/setForceStart" }, null),
            _ => (null, null)
        };
        if (endpoints is null) return DownloadClientHelpers.Unsupported(client, queueItemId, action, "qBittorrent");
        var pairs = new List<KeyValuePair<string, string>> { new("hashes", queueItemId) };
        if (deleteFiles is { } wipe)
        {
            pairs.Add(new("deleteFiles", wipe ? "true" : "false"));
        }
        if (action == DownloadClientActions.ForceStart)
        {
            pairs.Add(new("value", "true"));
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

    /// <summary>
    /// qBittorrent's "no estimate" is 8640000 seconds, exactly 100 days. It sends
    /// that whenever nothing is arriving, including the first seconds of every
    /// torrent. Read as a real estimate, it raised "unusually long" findings
    /// reading "Client ETA: 100.00:00:00" (#560). Zero is Deluno's "unknown".
    /// </summary>
    internal const long NoEstimateSeconds = 8_640_000;

    internal static int EtaSeconds(long? eta)
        => eta is null or <= 0 or >= NoEstimateSeconds ? 0 : (int)Math.Min(eta.Value, int.MaxValue);

    /// <summary>#811: qBittorrent sends -1/0 for "not yet" (no activity, not completed), which is null, not epoch.</summary>
    private static DateTimeOffset? FromUnixOrNull(long? value)
        => value is > 0 ? DateTimeOffset.FromUnixTimeSeconds(value.Value) : null;

    public override string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
    {
        var normalized = nativeStatus?.ToLowerInvariant() ?? string.Empty;
        // The data on disk is gone and this torrent cannot progress on its own
        // (#660, top priority): it used to match none of the checks below and
        // fall through to Downloading, an invisible "healthy" transfer that
        // never arrived.
        if (normalized == "missingfiles") return DownloadQueueStatuses.Stalled;
        // Verifying a hash or relocating finished files — no bytes are moving
        // on the wire either way, so this is its own state rather than
        // Downloading, which is what let the no-progress/speed rules (#659)
        // reason about throughput a checking or moving torrent cannot have.
        // Checked ahead of the progress-based ImportReady test below (#912):
        // qBittorrent reports progress 1.0 throughout a re-check or a move of
        // an already-complete torrent, and reading progress first raced the
        // import against a file qBittorrent still had open for relocation or
        // was still hashing.
        if (normalized.Contains("checking") || normalized == "moving") return DownloadQueueStatuses.NotTransferring;
        if ((progress ?? 0) >= 1 || normalized.Contains("upload")) return DownloadQueueStatuses.ImportReady;
        // A person stopped this, incomplete, download — distinct from
        // queuedDL, which is qBittorrent holding it back on its own.
        if (normalized is "pauseddl" or "stoppeddl") return DownloadQueueStatuses.Paused;
        if (normalized.Contains("pause") || normalized.Contains("queued")) return DownloadQueueStatuses.Queued;
        if (normalized.Contains("error") || normalized.Contains("stalled")) return DownloadQueueStatuses.Stalled;
        return DownloadQueueStatuses.Downloading;
    }

    /// <summary>
    /// qBittorrent's own word for what is wrong, in the one case it says so in
    /// the state string itself. <c>missingFiles</c> is the loudest of these —
    /// the payload is gone from disk — so it gets its own sentence rather than
    /// the raw state text.
    /// </summary>
    private static string? ErrorMessage(string? state)
        => string.Equals(state, "missingFiles", StringComparison.OrdinalIgnoreCase)
            ? "qBittorrent reports this torrent's data is missing from disk."
            : state?.Contains("error", StringComparison.OrdinalIgnoreCase) == true
                ? state
                : null;

    /// <summary>
    /// Whether qBittorrent's own state string is this machine's problem, not
    /// the release's (#880, #881): the payload missing from disk, or a bare
    /// "error" state (I/O, permissions, disk space — qBittorrent gives no
    /// finer detail than the word itself). A plain stalled/stalledDL state,
    /// with no error text, means no seeds or peers, which is a question about
    /// the release, so it is not a setup problem.
    /// </summary>
    private static bool IsSetupProblemState(string? state)
        => ErrorMessage(state) is not null;

    /// <summary>
    /// #811: qBittorrent v5.2.0+ added API-key (Bearer token) authentication,
    /// alongside the existing form-based cookie login every earlier version
    /// needs. Selected automatically: a saved client with no username but a
    /// secret set was never a meaningful form login (an empty username is not
    /// a real account), so that combination is read as "this secret is an API
    /// key" and sent as a bearer token instead of attempting to log in.
    /// </summary>
    private static Task AuthenticateAsync(HttpClient http, DownloadClientItem client, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(client.Username) && !string.IsNullOrWhiteSpace(client.Secret))
        {
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", client.Secret);
            return Task.CompletedTask;
        }

        return LoginAsync(http, client, cancellationToken);
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
        [property: JsonPropertyName("num_leechs")] int? NumLeechs,
        [property: JsonPropertyName("added_on")] long? AddedOn,
        [property: JsonPropertyName("save_path")] string? SavePath,
        [property: JsonPropertyName("content_path")] string? ContentPath,
        [property: JsonPropertyName("ratio")] double? Ratio = null,
        [property: JsonPropertyName("seeding_time")] long? SeedingTimeSeconds = null,
        // #815: added to torrents/info in a later WebAPI version than the
        // rest of this call's fields. Null on an older qBittorrent that does
        // not send it, which DownloadHealthPrivacyScopes reads as "unknown"
        // rather than guessing either way.
        [property: JsonPropertyName("private")] bool? Private = null,
        // #811 telemetry fields, all on the same torrents/info call.
        [property: JsonPropertyName("availability")] double? Availability = null,
        [property: JsonPropertyName("last_activity")] long? LastActivity = null,
        [property: JsonPropertyName("completion_on")] long? CompletionOn = null);

    private sealed record QbitMainData([property: JsonPropertyName("server_state")] QbitServerState? ServerState);

    private sealed record QbitTorrentFile([property: JsonPropertyName("name")] string? Name);

    private sealed record QbitServerState([property: JsonPropertyName("free_space_on_disk")] long? FreeSpaceOnDisk);

    /// <summary>qBittorrent's per-tracker status: 4 is "Not working", and only then does <c>msg</c> carry the tracker's own reason.</summary>
    private sealed record QbitTrackerItem(
        [property: JsonPropertyName("status")] int? Status,
        [property: JsonPropertyName("msg")] string? Message);

    private const int TrackerNotWorking = 4;

    /// <summary>
    /// #878: qBittorrent has no tracker field on <c>torrents/info</c> at all —
    /// its answer is only ever the queue-status word, never the tracker's own
    /// message, which is why it could not tell an unregistered torrent apart
    /// from an ordinary stall (cleanup sheet C5). This is the one extra
    /// request, per torrent, that reads what the tracker actually said.
    /// </summary>
    private static async Task<Dictionary<string, string>> GetTrackerStatusesAsync(
        HttpClient http, IReadOnlyList<QbitTorrentItem> torrents, CancellationToken cancellationToken)
    {
        var statusByHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hash in torrents.Select(item => item.Hash).Where(hash => !string.IsNullOrWhiteSpace(hash)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var trackers = await http.GetFromJsonAsync<IReadOnlyList<QbitTrackerItem>>($"api/v2/torrents/trackers?hash={hash}", cancellationToken) ?? [];
                statusByHash[hash!] = trackers.Any(tracker => tracker.Status == TrackerNotWorking && DownloadClientHelpers.LooksUnregistered(tracker.Message))
                    ? DownloadTrackerStatuses.Unregistered
                    : DownloadTrackerStatuses.Ok;
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
            {
                // A tracker read that fails says nothing either way (#901) -
                // an outage never reads as "unregistered".
                statusByHash[hash!] = DownloadTrackerStatuses.Unknown;
            }
        }

        return statusByHash;
    }
}
