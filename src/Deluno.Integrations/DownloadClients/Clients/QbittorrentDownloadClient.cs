using System.Collections.Concurrent;
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
        handlerFactory ?? (() => new SocketsHttpHandler { ConnectCallback = Deluno.Infrastructure.Http.LoopbackFirstConnection.ConnectAsync, CookieContainer = new CookieContainer() });

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
        // #999: content layout, sent only when this client has one chosen.
        // Radarr and Sonarr's QBittorrentProxyV2 send "Original" and
        // "Subfolder" the same way and send nothing for "use qBittorrent's
        // own setting"; the API also takes "NoSubfolder" (files straight into
        // the save folder), which their settings leave out.
        if (ContentLayouts.ToQbittorrentValue(client.ContentLayout) is { } layout)
        {
            addTimePairs.Add(new("contentLayout", layout));
        }
        // #811: savepath, when this client has one configured -- sent at add
        // time same as the others, and reapplied with setLocation once the
        // hash is known below, since qBittorrent's own Automatic Torrent
        // Management can otherwise silently override the add-time value.
        if (!string.IsNullOrWhiteSpace(client.Location))
        {
            addTimePairs.Add(new("savepath", client.Location));
        }
        else
        {
            // No folder of Deluno's own: the category's save folder is the one the library watches, and
            // qBittorrent only uses it for a torrent it manages automatically. Asked for here so it holds
            // whatever qBittorrent's default mode is (golden path, rc.30.8: grabs landed in the completed root).
            addTimePairs.Add(new("autoTMM", "true"));
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
        // qBittorrent 5.1 and later answer 409 when nothing was added, which is
        // also how it reports a torrent it already holds; that must reach the
        // duplicate check below rather than throw as if the client were down.
        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            response.EnsureSuccessStatusCode();
        }
        var payload = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        var knownHash = DownloadClientHelpers.KnownHashOf(request);
        var alreadyHeld = knownHash is not null && before is not null && before.Contains(knownHash, StringComparer.OrdinalIgnoreCase);

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
        if (!alreadyHeld && (payload.StartsWith("Fails", StringComparison.OrdinalIgnoreCase) || response.StatusCode == HttpStatusCode.Conflict))
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
        // #1062: Deluno knows the info-hash before it sends (from the magnet, or
        // from the .torrent's own bytes), so the grab's id is that hash. What
        // qBittorrent lists afterwards is only a confirmation, never a place to
        // pick an item out of by position.
        var after = await WaitForNewHashAsync(http, before, knownHash, cancellationToken);

        // A hash still unlisted after the 6 second window, on an answer that did
        // not say it took the torrent, is looked for once more at about 30
        // seconds: a client that answers something else and never lists the
        // torrent rejected it silently, and waiting out the 2 hour grab timeout
        // to learn that helps nobody (#1062).
        if (knownHash is not null && after is not null && !after.Contains(knownHash, StringComparer.OrdinalIgnoreCase)
            && !AnswerSaysItWasTaken(payload, knownHash))
        {
            await Task.Delay(ExtendedConfirmationDelay, cancellationToken);
            after = await ListHashesAsync(http, cancellationToken) ?? after;
            if (!after.Contains(knownHash, StringComparer.OrdinalIgnoreCase))
            {
                return DownloadClientHelpers.GrabFailure(
                    client,
                    request,
                    $"qBittorrent answered \"{payload.Trim()}\" and never listed the torrent, so it turned the release down. Its log says why.") with
                {
                    ResponseCode = (int)response.StatusCode,
                    ResponseJson = payload
                };
            }
        }
        if (knownHash is not null && before is not null && before.Contains(knownHash, StringComparer.OrdinalIgnoreCase)
            || knownHash is null && before is not null && after is not null && !after.Except(before).Any())
        {
            // No new torrent is also what a link qBittorrent could not read
            // looks like: it logs "Failed to add torrent" and still answered
            // Ok. Only a hash Deluno knows, already in the list, is a duplicate.
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

            return DownloadClientHelpers.GrabFailure(
                client,
                request,
                "qBittorrent accepted the request but added no torrent. Either it already holds this release (remove it there, or force a re-download), or it could not read what the link returned; its log says which.") with
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
        // A release qBittorrent never shows within the window still has its id
        // (the hash Deluno sent); the poller ends it as "never showed it" the
        // same as for every other client.
        var added = knownHash ?? DownloadClientHelpers.TheOnlyNewItem(before, after);

        // #811: setShareLimits, once the hash is known. Best-effort -- a
        // failure here does not fail the grab, since the download itself
        // already succeeded; the client just keeps its own default limits.
        var listed = added is not null && after is not null && after.Contains(added, StringComparer.OrdinalIgnoreCase);
        if (listed && HasShareLimits(client))
        {
            await TrySetShareLimitsAsync(http, client, added!, cancellationToken);
        }

        // #811: setCategory and setLocation, once the hash is known — the
        // same explicit-write path setShareLimits above uses, so a category
        // or save path Deluno resolved is not left to whatever the add-time
        // form field happened to take (qBittorrent's Automatic Torrent
        // Management can override an add-time savepath; the explicit call
        // does not go through it the same way).
        if (listed)
        {
            await TrySetCategoryAsync(http, client, request, added!, cancellationToken);
            if (!string.IsNullOrWhiteSpace(client.Location))
            {
                await TrySetLocationAsync(http, client, added!, cancellationToken);
            }
            else
            {
                await TrySetAutoManagementAsync(http, added!, cancellationToken);
            }
        }

        return DownloadClientHelpers.GrabSuccess(client, request, "Release URL sent to qBittorrent.") with
        {
            ExternalId = added
        };
    }

    /// <summary>How much longer than the first 6 second window a silent rejection is waited on.</summary>
    internal static TimeSpan ExtendedConfirmationDelay { get; set; } = TimeSpan.FromSeconds(24);

    /// <summary>
    /// Whether qBittorrent's answer to an add says it took the torrent: "Ok." up
    /// to 5.0, or from 5.1 a JSON object whose success or pending count is above
    /// zero, or whose added ids name this hash.
    /// </summary>
    internal static bool AnswerSaysItWasTaken(string payload, string knownHash)
    {
        if (payload.StartsWith("Ok", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!payload.StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.TryGetProperty("added_torrent_ids", out var ids) && ids.ValueKind == JsonValueKind.Array
                && ids.EnumerateArray().Any(id => id.ValueKind == JsonValueKind.String && string.Equals(id.GetString(), knownHash, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return CountAbove(root, "success_count") || CountAbove(root, "pending_count");
        }
        catch (JsonException)
        {
            return false;
        }

        static bool CountAbove(JsonElement root, string name)
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count > 0;
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
    /// Automatic Torrent Management on for the torrent just added, so it saves into its category's folder.
    /// Setting the category after the add can leave a torrent on manual management, and a manual torrent
    /// keeps its own save path; this makes the category folder the one in force. Best-effort.
    /// </summary>
    private static async Task TrySetAutoManagementAsync(HttpClient http, string hash, CancellationToken cancellationToken)
    {
        try
        {
            var pairs = new[]
            {
                new KeyValuePair<string, string>("hashes", hash),
                new KeyValuePair<string, string>("enable", "true")
            };
            using var response = await http.PostAsync("api/v2/torrents/setAutoManagement", new FormUrlEncodedContent(pairs), cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Left alone: the add-time autoTMM field already asked for it.
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

        // One session per client, kept between polls (#1081): the cookie stays,
        // and the sign-in happens once, then again only when qBittorrent says
        // the session is gone (401/403), instead of a new connection and a login
        // on every poll.
        var session = GetSession(client, baseUri);
        await session.EnsureSignedInAsync(() => AuthenticateAsync(session.Http, client, cancellationToken));
        try
        {
            return await ReadSnapshotAsync(session.Http, client, baseUri, capturedUtc, cancellationToken);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            session.SignedOut();
            await session.EnsureSignedInAsync(() => AuthenticateAsync(session.Http, client, cancellationToken));
            return await ReadSnapshotAsync(session.Http, client, baseUri, capturedUtc, cancellationToken);
        }
    }

    private readonly ConcurrentDictionary<string, QbitSession> sessions = new(StringComparer.OrdinalIgnoreCase);

    private QbitSession GetSession(DownloadClientItem client, Uri baseUri)
    {
        var fingerprint = $"{baseUri}|{client.Username}|{client.Secret}";
        while (true)
        {
            if (sessions.TryGetValue(client.Id, out var existing))
            {
                if (existing.Fingerprint == fingerprint)
                {
                    return existing;
                }

                // Address or credentials changed: start again.
                if (sessions.TryUpdate(client.Id, CreateSession(fingerprint, baseUri), existing))
                {
                    existing.Dispose();
                }

                continue;
            }

            var created = CreateSession(fingerprint, baseUri);
            if (sessions.TryAdd(client.Id, created))
            {
                return created;
            }

            created.Dispose();
        }
    }

    private QbitSession CreateSession(string fingerprint, Uri baseUri)
    {
        var handler = handlerFactory();
        return new QbitSession(fingerprint, new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(8) });
    }

    private sealed class QbitSession(string fingerprint, HttpClient http) : IDisposable
    {
        private readonly SemaphoreSlim signInGate = new(1, 1);
        private volatile bool signedIn;

        public string Fingerprint { get; } = fingerprint;

        public HttpClient Http { get; } = http;

        public void SignedOut() => signedIn = false;

        public async Task EnsureSignedInAsync(Func<Task> signIn)
        {
            if (signedIn)
            {
                return;
            }

            await signInGate.WaitAsync();
            try
            {
                if (!signedIn)
                {
                    await signIn();
                    signedIn = true;
                }
            }
            finally
            {
                signInGate.Release();
            }
        }

        // Left to the garbage collector when a call is still using it; the
        // handler holds only a connection and a cookie jar.
        public void Dispose() { }
    }

    // How long a tracker verdict for one torrent is reused (#1081).
    private static readonly TimeSpan TrackerStatusTtl = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, (string Status, DateTimeOffset At)> trackerStatusCache = new(StringComparer.OrdinalIgnoreCase);
    // #1194: every announce host of a torrent whose tracker list was read, kept beside its verdict.
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> trackerHostsCache = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<string> TrackerHostsOf(string clientId, QbitTorrentItem item)
    {
        var hosts = new List<string>();
        if (DownloadClientHelpers.HostOf(item.Tracker) is { } current) hosts.Add(current);
        if (item.Hash is { Length: > 0 } hash && trackerHostsCache.TryGetValue($"{clientId}|{hash}", out var known))
        {
            hosts.AddRange(known.Where(host => !hosts.Contains(host, StringComparer.OrdinalIgnoreCase)));
        }
        return hosts;
    }

    private async Task<DownloadClientTelemetrySnapshot?> ReadSnapshotAsync(
        HttpClient http, DownloadClientItem client, Uri baseUri, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        // The two reads are independent, so they go together.
        var serverStateTask = GetServerStateAsync(http, cancellationToken);
        var versionTask = GetVersionAsync(http, cancellationToken);
        var torrents = await http.GetFromJsonAsync<IReadOnlyList<QbitTorrentItem>>("api/v2/torrents/info", cancellationToken) ?? [];

        var trackerStatusByHash = await GetTrackerStatusesAsync(http, client.Id, torrents, capturedUtc, cancellationToken);
        var queue = torrents.Select(item => new DownloadQueueItem(
            item.Hash ?? item.Name ?? Guid.CreateVersion7().ToString("N"), client.Id, client.Name, client.Protocol,
            DownloadClientHelpers.InferMediaType(client, item.Category), DownloadClientHelpers.CleanReleaseTitle(item.Name ?? "Unknown qBittorrent item"),
            item.Name ?? "Unknown qBittorrent item", item.Category ?? string.Empty, NormalizeStatus(item.State, item.Progress),
            Math.Clamp(((item.Progress ?? 0) * 100), 0, 100), ((item.DownloadSpeed ?? 0) / 1_000_000d),
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
            UploadSpeedMbps: ((item.UploadSpeed ?? 0) / 1_000_000d),
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
            LastActivityUtc: DownloadClientHelpers.FromUnixOrNull(item.LastActivity),
            CompletionUtc: DownloadClientHelpers.FromUnixOrNull(item.CompletionOn),
            // #951: qBittorrent's own metaDL state - a magnet that has not
            // resolved its own metadata (name, file list, size) yet. NormalizeStatus
            // maps metaDL to Downloading (it has no "fetching metadata" status of
            // its own), so this is read from the raw state string directly.
            IsFetchingMetadata: string.Equals(item.State, "metaDL", StringComparison.OrdinalIgnoreCase),
            // #1194: for the ignore list. torrents/info names the tracker in use; the full list is added when it was read.
            Tags: DownloadClientHelpers.SplitTags(item.Tags),
            TrackerHosts: TrackerHostsOf(client.Id, item)))
            .Select(item => DownloadClientHelpers.ReconcileTorrentStatus(item, capturedUtc))
            .ToArray();
        var (freeSpaceBytes, alternateSpeedLimitEnabled) = await serverStateTask;
        var (health, message) = DownloadClientHelpers.EvaluateDiskSpaceHealth(
            $"Connected to qBittorrent at {baseUri.Host}:{baseUri.Port}.",
            freeSpaceBytes,
            null);
        return CreateSnapshot(client, queue, capturedUtc, health, message, freeSpaceBytes: freeSpaceBytes, clientVersion: await versionTask, alternateSpeedLimitEnabled: alternateSpeedLimitEnabled);
    }

    /// <summary>qBittorrent's own version (#1117), from <c>api/v2/app/version</c>. Null when it will not say.</summary>
    private static async Task<string?> GetVersionAsync(HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            return Deluno.Connections.Versions.ConnectionVersionParsers.FromQbittorrent(await http.GetStringAsync("api/v2/app/version", cancellationToken));
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>server_state.free_space_on_disk</c> and <c>server_state.use_alt_speed_limits</c>
    /// from qBittorrent's sync call (#909, #951) -- a second, cheap request
    /// rather than folding this into <c>torrents/info</c>, which has neither
    /// field. A client that will not answer this at all (an old version, or
    /// a momentary hiccup) reports nothing rather than a false low-space
    /// warning or a false "not throttled" reading.
    /// </summary>
    private static async Task<(long? FreeSpaceBytes, bool? AlternateSpeedLimitEnabled)> GetServerStateAsync(HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            var mainData = await http.GetFromJsonAsync<QbitMainData>("api/v2/sync/maindata", cancellationToken);
            return (mainData?.ServerState?.FreeSpaceOnDisk, mainData?.ServerState?.UseAltSpeedLimits);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return (null, null);
        }
    }

    /// <summary>qBittorrent's temporary path when "keep incomplete torrents in" is on; otherwise the save path, where it then writes them (#1164). Null when it cannot be read.</summary>
    private static async Task<string?> ReadIncompletePathAsync(HttpClient http, string? savePath, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync("api/v2/app/preferences", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var enabled = root.TryGetProperty("temp_path_enabled", out var flag) && flag.ValueKind == JsonValueKind.True;
            var temp = root.TryGetProperty("temp_path", out var path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null;
            return enabled && !string.IsNullOrWhiteSpace(temp) ? temp : savePath;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
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
            using var handler = this.handlerFactory();
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

            // The folder a torrent really lands in is not always the category's. Deluno sends this client's own
            // Location with every torrent, and qBittorrent saves it there whatever the category says; without
            // one Deluno asks for automatic management so the category's folder is the one in force. Report
            // the folder in force, so the check cannot say Matches while torrents land somewhere else.
            if (!string.IsNullOrWhiteSpace(client.Location))
            {
                var location = client.Location.Trim();
                var incomplete = await ReadIncompletePathAsync(http, location, cancellationToken);
                return new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Ready,
                    $"Deluno sends every torrent to {location} in qBittorrent, whatever its {normalizedCategory} category says.",
                    Supported: true, Found: true, SavePath: location, IncompletePath: incomplete);
            }

            // Where a torrent is written while it downloads (#1164): the temporary path when qBittorrent
            // keeps incomplete torrents apart, otherwise the save path itself.
            var incompletePath = await ReadIncompletePathAsync(http, savePath, cancellationToken);
            return string.IsNullOrWhiteSpace(savePath)
                ? new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Configuration,
                    $"qBittorrent has a category named {normalizedCategory}, but it has no save path, so downloads will go wherever qBittorrent defaults to rather than a folder Deluno watches. Set a save path on the category in qBittorrent.",
                    Supported: true, Found: true, SavePath: null, IncompletePath: incompletePath)
                : new(client.Id, client.Name, normalizedCategory, DownloadClientCategoryStatuses.Ready,
                    $"qBittorrent has a category named {normalizedCategory}, saving to {savePath}.",
                    Supported: true, Found: true, SavePath: savePath, IncompletePath: incompletePath);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
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

    /// <summary>
    /// #1240: moves a torrent to the keep-seeding category with <c>setCategory</c>, and nothing else. A torrent that
    /// is on Automatic Torrent Management is taken off it first, because with it on qBittorrent ties the folder to the
    /// category and a category change can relocate the data; a torrent already on manual management is not touched.
    /// qBittorrent refuses a category it does not have (409), and Deluno never creates one.
    /// </summary>
    public override async Task<DownloadClientActionResult> SetCategoryAsync(
        DownloadClientItem client, string queueItemId, string category, string? currentCategory, CancellationToken cancellationToken)
    {
        var action = DownloadClientActions.SetCategory;
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.MissingAddress(client, queueItemId, action);
        using var handler = this.handlerFactory();
        using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(8) };
        await AuthenticateAsync(http, client, cancellationToken);

        async Task<(ClientCategoryReading Reading, bool AutoManaged)> ReadAsync()
        {
            using var response = await http.GetAsync($"api/v2/torrents/info?hashes={Uri.EscapeDataString(queueItemId)}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return (new ClientCategoryReading(ClientHas.Unknown, null), false);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return (new ClientCategoryReading(ClientHas.Unknown, null), false);
            }

            var torrent = document.RootElement.EnumerateArray().FirstOrDefault();
            if (torrent.ValueKind != JsonValueKind.Object)
            {
                return (new ClientCategoryReading(ClientHas.No, null), false);
            }

            var reported = torrent.TryGetProperty("category", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var auto = torrent.TryGetProperty("auto_tmm", out var autoValue) && autoValue.ValueKind == JsonValueKind.True;
            return (new ClientCategoryReading(ClientHas.Yes, reported), auto);
        }

        var autoManaged = false;
        return await ClientCategoryMove.RunAsync(
            client, queueItemId, category, "qBittorrent",
            async () =>
            {
                var (reading, auto) = await ReadAsync();
                autoManaged = auto;
                return reading;
            },
            async _ =>
            {
                if (autoManaged)
                {
                    using var off = await http.PostAsync(
                        "api/v2/torrents/setAutoManagement",
                        new FormUrlEncodedContent([new("hashes", queueItemId), new("enable", "false")]),
                        cancellationToken);
                    if (!off.IsSuccessStatusCode)
                    {
                        return DownloadClientHelpers.ActionFailure(client, queueItemId, action,
                            $"qBittorrent would not switch off Automatic Torrent Management ({(int)off.StatusCode}), so Deluno did not change the category: it could have moved the files.",
                            off.StatusCode);
                    }
                }

                using var response = await http.PostAsync(
                    "api/v2/torrents/setCategory",
                    new FormUrlEncodedContent([new("hashes", queueItemId), new("category", category)]),
                    cancellationToken);
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    return DownloadClientHelpers.ActionFailure(client, queueItemId, action,
                        $"qBittorrent does not have a category named {category}. Create it in qBittorrent first; Deluno does not create categories.",
                        response.StatusCode);
                }

                return response.IsSuccessStatusCode
                    ? DownloadClientHelpers.ActionSuccess(client, queueItemId, action, "qBittorrent action sent.")
                    : DownloadClientHelpers.ActionFailure(client, queueItemId, action, $"qBittorrent returned {(int)response.StatusCode} when asked to set the category.", response.StatusCode);
            });
    }

    public override async Task<DownloadClientCategoryCheckResult> CheckKeepSeedingCategoryAsync(
        DownloadClientItem client, string category, CancellationToken cancellationToken)
    {
        var name = category.Trim();
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (name.Length == 0 || baseUri is null)
        {
            return new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Configuration,
                name.Length == 0 ? "Enter a category before checking it." : "Add the qBittorrent address before checking a category.", Supported: true, Found: false);
        }

        try
        {
            using var handler = this.handlerFactory();
            using var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(8) };
            await AuthenticateAsync(http, client, cancellationToken);
            using var response = await http.GetAsync("api/v2/torrents/categories", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Unsupported,
                    "This qBittorrent does not list its categories, so Deluno cannot check this one. It will still try the move.", Supported: false, Found: false);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Unreachable,
                    $"qBittorrent returned {(int)response.StatusCode} while checking its categories.", Supported: true, Found: false);
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var found = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            return found
                ? new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Ready,
                    $"qBittorrent has a category named {name}.", Supported: true, Found: true)
                : new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Missing,
                    $"qBittorrent does not have a category named {name}. Create it in qBittorrent first; Deluno does not create categories.", Supported: true, Found: false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        {
            return new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Unreachable,
                $"Deluno could not reach qBittorrent: {exception.Message}", Supported: true, Found: false);
        }
    }

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
        if (normalized.Contains("error")) return DownloadQueueStatuses.Stalled;
        // stalledDL is the client's own flag, not proof (#1193): Downloading until Deluno's own no-progress window
        // (DownloadClientHelpers.ReconcileTorrentStatus) finds no byte gain, whatever the percent.
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
        // The same sign-in the connection test uses (#1061): a login the test accepted is never refused here.
        var result = await Deluno.Connections.QbittorrentSignIn.SignInAsync(http, client.Username, client.Secret, cancellationToken);
        if (result.SignedIn) return;
        if (!result.Rejected)
        {
            throw new HttpRequestException($"qBittorrent answered the sign-in with {(int)result.Status}.", null, result.Status);
        }

        throw new InvalidOperationException("qBittorrent rejected the configured username/password.");
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
        string? knownHash,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(6);
        HashSet<string>? after = null;

        while (true)
        {
            after = await ListHashesAsync(http, cancellationToken);

            if (before is null || after is null
                || (knownHash is null ? after.Except(before).Any() : after.Contains(knownHash, StringComparer.OrdinalIgnoreCase)))
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
        [property: JsonPropertyName("completion_on")] long? CompletionOn = null,
        // #1194: qBittorrent's comma-separated tags, and the tracker it is announcing to now (empty when none works).
        [property: JsonPropertyName("tags")] string? Tags = null,
        [property: JsonPropertyName("tracker")] string? Tracker = null);

    private sealed record QbitMainData([property: JsonPropertyName("server_state")] QbitServerState? ServerState);

    private sealed record QbitTorrentFile([property: JsonPropertyName("name")] string? Name);

    private sealed record QbitServerState(
        [property: JsonPropertyName("free_space_on_disk")] long? FreeSpaceOnDisk,
        [property: JsonPropertyName("use_alt_speed_limits")] bool? UseAltSpeedLimits);

    /// <summary>qBittorrent's per-tracker status: 4 is "Not working", and only then does <c>msg</c> carry the tracker's own reason.</summary>
    private sealed record QbitTrackerItem(
        [property: JsonPropertyName("status")] int? Status,
        [property: JsonPropertyName("msg")] string? Message,
        [property: JsonPropertyName("url")] string? Url = null);

    private const int TrackerNotWorking = 4;

    /// <summary>
    /// #878: qBittorrent has no tracker field on <c>torrents/info</c> at all —
    /// its answer is only ever the queue-status word, never the tracker's own
    /// message, which is why it could not tell an unregistered torrent apart
    /// from an ordinary stall (cleanup sheet C5). This reads what the tracker
    /// actually said.
    ///
    /// <para>#1081: that used to be one request per torrent on every poll, one
    /// after another. A torrent that is moving data right now has a working
    /// tracker by any test that matters, so only the ones that are not
    /// (stalled, errored, idle, queued) are asked, each verdict is kept for a
    /// minute, and the asking happens in parallel.</para>
    /// </summary>
    private async Task<Dictionary<string, string>> GetTrackerStatusesAsync(
        HttpClient http, string clientId, IReadOnlyList<QbitTorrentItem> torrents, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var statusByHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var toAsk = new List<string>();
        foreach (var torrent in torrents.Where(item => !string.IsNullOrWhiteSpace(item.Hash)).DistinctBy(item => item.Hash, StringComparer.OrdinalIgnoreCase))
        {
            var hash = torrent.Hash!;
            var key = $"{clientId}|{hash}";
            if (trackerStatusCache.TryGetValue(key, out var cached) && now - cached.At < TrackerStatusTtl)
            {
                statusByHash[hash] = cached.Status;
            }
            else if ((torrent.DownloadSpeed ?? 0) > 0 || (torrent.UploadSpeed ?? 0) > 0)
            {
                // Moving data: nothing to ask, and nothing to claim either.
                statusByHash[hash] = DownloadTrackerStatuses.Unknown;
            }
            else
            {
                toAsk.Add(hash);
            }
        }

        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(toAsk.Select(async hash =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                string status;
                try
                {
                    var trackers = await http.GetFromJsonAsync<IReadOnlyList<QbitTrackerItem>>($"api/v2/torrents/trackers?hash={hash}", cancellationToken) ?? [];
                    status = trackers.Any(tracker => tracker.Status == TrackerNotWorking && DownloadClientHelpers.LooksUnregistered(tracker.Message))
                        ? DownloadTrackerStatuses.Unregistered
                        : DownloadTrackerStatuses.Ok;
                    trackerStatusCache[$"{clientId}|{hash}"] = (status, now);
                    trackerHostsCache[$"{clientId}|{hash}"] = DownloadClientHelpers.HostsOf(trackers.Select(tracker => tracker.Url));
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    // A tracker read that fails says nothing either way (#901) -
                    // an outage never reads as "unregistered". Not remembered.
                    status = DownloadTrackerStatuses.Unknown;
                }

                lock (statusByHash)
                {
                    statusByHash[hash] = status;
                }
            }
            finally
            {
                gate.Release();
            }
        }));

        // Forget torrents that are gone, so the memory cannot grow forever.
        if (trackerStatusCache.Count > torrents.Count + 64)
        {
            var live = torrents.Select(item => $"{clientId}|{item.Hash}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in trackerStatusCache.Keys.Where(key => key.StartsWith(clientId + "|", StringComparison.OrdinalIgnoreCase) && !live.Contains(key)).ToArray())
            {
                trackerStatusCache.TryRemove(key, out _);
                trackerHostsCache.TryRemove(key, out _);
            }
        }

        return statusByHash;
    }
}
