using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Connections.Contracts;
using Deluno.Contracts;

namespace Deluno.Integrations.DownloadClients.Clients;

/// <param name="handlerFactory">
/// Supplied only by tests. uTorrent needs a cookie container and a credential
/// on its handler, so this class builds its own rather than taking an
/// IHttpClientFactory — which left it unreachable from a test without a real
/// server on localhost. The default is exactly what it built before.
/// </param>
public sealed class UTorrentDownloadClient(Func<HttpMessageHandler>? handlerFactory = null) : DownloadClientBase
{
    private readonly Func<DownloadClientItem, HttpMessageHandler> handler = BuildHandler(handlerFactory);

    private static Func<DownloadClientItem, HttpMessageHandler> BuildHandler(Func<HttpMessageHandler>? factory)
        => factory is null
            ? client => new SocketsHttpHandler
            {
                ConnectCallback = Deluno.Infrastructure.Http.LoopbackFirstConnection.ConnectAsync,
                CookieContainer = new CookieContainer(),
                Credentials = DownloadClientHelpers.BuildCredential(client)
            }
            : _ => factory();

    public override string Protocol => "utorrent";
    public override DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, false, true, true, true, false, "basic-token");

    /// <summary>
    /// The WebUI `list` status bitfield (`list` index 1). uTorrent has no
    /// native status string; this is the only signal there is (#728).
    /// </summary>
    private static class UTorrentStatus
    {
        internal const int Started = 0x1;
        internal const int Checking = 0x2;
        internal const int StartAfterCheck = 0x4;
        internal const int Checked = 0x8;
        internal const int Error = 0x10;
        internal const int Paused = 0x20;
        internal const int Queued = 0x40;
        internal const int Loaded = 0x80;
    }

    public override async Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.GrabFailure(client, request, "Download client address is missing.");
        using var http = CreateHttp(client, baseUri, TimeSpan.FromSeconds(10));
        var token = await DownloadClientHelpers.GetUTorrentTokenAsync(http, cancellationToken);

        // uTorrent's add-url/add-file verbs take no category parameter of
        // their own (#728), so the label has to be set with a follow-up
        // setprops call once the torrent exists — which needs its hash, which
        // the add call does not return either. Same shape as qBittorrent's
        // own before/after hash diff (#606).
        var before = await ListHashesAsync(http, token, cancellationToken);

        string successMessage;
        if (request.TorrentFile is { } file)
        {
            // A .torrent Deluno fetched with a tracker's sign-in (#606).
            using var upload = new MultipartFormDataContent { { new ByteArrayContent(file), "torrent_file", "release.torrent" } };
            using var uploaded = await http.PostAsync($"gui/?token={Uri.EscapeDataString(token)}&action=add-file", upload, cancellationToken);
            uploaded.EnsureSuccessStatusCode();
            successMessage = "Torrent file sent to uTorrent.";
        }
        else
        {
            using var response = await http.GetAsync($"gui/?token={Uri.EscapeDataString(token)}&action=add-url&s={Uri.EscapeDataString(request.DownloadUrl)}", cancellationToken);
            response.EnsureSuccessStatusCode();
            successMessage = "Release URL sent to uTorrent.";
        }

        // #1062: the hash is known before the send (magnet, or the .torrent's own
        // bytes), so the grab's id is that hash and uTorrent's list only confirms
        // it. uTorrent never returns success with no id: a release whose hash
        // Deluno could not know is only followed when exactly one torrent
        // appeared because of it.
        var knownHash = DownloadClientHelpers.KnownHashOf(request);
        if (knownHash is not null)
        {
            if (before is not null && before.Contains(knownHash, StringComparer.OrdinalIgnoreCase))
            {
                return DownloadClientHelpers.GrabSuccess(
                    client,
                    request,
                    "uTorrent already holds this release. Deluno is following the copy it has instead of sending another.") with
                {
                    ExternalId = knownHash,
                    IsDuplicateFollow = true
                };
            }

            var listed = await WaitForHashAsync(http, token, before, knownHash, cancellationToken);
            if (listed is not null)
            {
                await TryLabelAsync(http, token, listed, DownloadClientHelpers.ResolveCategory(client, request), cancellationToken);
            }

            return DownloadClientHelpers.GrabSuccess(client, request, successMessage) with { ExternalId = knownHash };
        }

        var added = await WaitForHashAsync(http, token, before, null, cancellationToken);
        if (added is null)
        {
            return DownloadClientHelpers.GrabFailure(
                client,
                request,
                "uTorrent accepted the release, but Deluno could not tell which torrent it became, so it is not following it. Check uTorrent's list; if it is there, remove it and search again.");
        }

        await TryLabelAsync(http, token, added, DownloadClientHelpers.ResolveCategory(client, request), cancellationToken);
        return DownloadClientHelpers.GrabSuccess(client, request, successMessage) with { ExternalId = added.ToLowerInvariant() };
    }

    /// <summary>
    /// Best-effort: uTorrent already has the torrent at this point, so a failed
    /// label set is not a grab failure, only an unlabeled item for
    /// InferMediaType to fall back on.
    /// </summary>
    private static async Task TryLabelAsync(HttpClient http, string token, string hash, string category, CancellationToken cancellationToken)
    {
        try
        {
            using var labelResponse = await http.GetAsync(
                $"gui/?token={Uri.EscapeDataString(token)}&action=setprops&hash={Uri.EscapeDataString(hash)}&s=label&v={Uri.EscapeDataString(category)}",
                cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
        }
    }

    public override async Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return null;
        using var http = CreateHttp(client, baseUri, TimeSpan.FromSeconds(8));
        var token = await DownloadClientHelpers.GetUTorrentTokenAsync(http, cancellationToken);
        var payload = await http.GetFromJsonAsync<UTorrentListResponse>($"gui/?token={Uri.EscapeDataString(token)}&list=1", cancellationToken);
        var torrents = payload?.Torrents ?? [];
        var seedingMinutesByHash = await GetSeedingMinutesAsync(http, token, torrents, cancellationToken);
        var queue = torrents.Select(item =>
        {
            var hash = item.Count > 0 ? AsString(item[0]) : Guid.CreateVersion7().ToString("N");
            var statusBits = item.Count > 1 ? Convert.ToInt32(AsDouble(item[1])) : 0;
            var name = item.Count > 2 ? AsString(item[2]) : "Unknown uTorrent item";
            var size = item.Count > 3 ? AsInt64(item[3]) : 0;
            var progress = item.Count > 4 ? (AsDouble(item[4]) / 10d) : 0;
            // uTorrent's list array is positional: index 8 is upload rate in
            // bytes per second, index 9 is download.
            var uploadSpeed = item.Count > 8 ? AsDouble(item[8]) / 1_000_000d : 0;
            var speed = item.Count > 9 ? AsDouble(item[9]) / 1_000_000d : 0;
            var category = item.Count > 11 ? AsString(item[11]) : string.Empty;
            var ratioPerMille = item.Count > 7 ? (double?)AsDouble(item[7]) : null;
            var statusMessage = item.Count > 21 ? AsString(item[21]) : string.Empty;
            var savePath = item.Count > 26 ? AsString(item[26]) : null;

            var status = NormalizeStatus(statusBits.ToString(CultureInfo.InvariantCulture), progress);
            var errored = (statusBits & UTorrentStatus.Error) != 0;
            // #943 (Phase 4 re-check of C5): uTorrent's status bitfield gives no
            // tracker-vs-local distinction of its own, but the status message
            // that comes with an errored torrent is the tracker's own text when
            // it rejected the torrent outright — the same wording qBittorrent,
            // Transmission, Deluge and rTorrent already read with
            // LooksUnregistered. That case is a release-side finding, not a
            // problem on this machine.
            var looksUnregistered = errored && DownloadClientHelpers.LooksUnregistered(statusMessage);

            var queueItem = new DownloadQueueItem(hash, client.Id, client.Name, client.Protocol, DownloadClientHelpers.InferMediaType(client, category), DownloadClientHelpers.CleanReleaseTitle(name), name,
                category, status,
                Math.Clamp(progress, 0, 100), (speed), item.Count > 10 ? Math.Max(0, Convert.ToInt32(AsDouble(item[10]))) : 0, size,
                (long)(size * (progress / 100d)), (item.Count > 12 ? Convert.ToInt32(AsDouble(item[12])) : 0) + (item.Count > 14 ? Convert.ToInt32(AsDouble(item[14])) : 0), "uTorrent",
                errored && !string.IsNullOrWhiteSpace(statusMessage) ? statusMessage : null,
                item.Count > 23 ? DownloadClientHelpers.FromUnix(Convert.ToInt64(AsDouble(item[23]))) : capturedUtc,
                SourcePath: string.IsNullOrWhiteSpace(savePath) ? null : savePath,
                UploadSpeedMbps: (uploadSpeed),
                Ratio: ratioPerMille is null ? null : Math.Round(ratioPerMille.Value / 1000d, 3),
                SeedingMinutes: seedingMinutesByHash.TryGetValue(hash, out var minutes) ? minutes : null,
                // #880/#881: uTorrent's status bitfield gives no tracker-vs-local
                // distinction the way Transmission's error code does — the Error
                // bit is the only signal, same as qBittorrent's bare "error"
                // state, so it is read the same way: a problem on this machine
                // (disk, permissions), not the release — unless the tracker's own
                // message says the release itself is gone (#943), which is never
                // a setup problem.
                IsSetupProblem: errored && !looksUnregistered,
                TrackerStatus: looksUnregistered ? DownloadTrackerStatuses.Unregistered : DownloadTrackerStatuses.Unknown);

            // The one rule every torrent adapter applies (#650). uTorrent's own
            // status is decoded above rather than guessed from progress/speed,
            // but the no-metadata/no-peers stall window is shared, not
            // reimplemented here (#659 owns this function's own logic).
            return DownloadClientHelpers.ReconcileTorrentStatus(queueItem, capturedUtc);
        }).ToArray();
        return CreateSnapshot(client, queue, capturedUtc, "healthy", $"Connected to uTorrent at {baseUri.Host}:{baseUri.Port}.");
    }

    public override async Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
    {
        // "removedata" is uTorrent's remove-the-file-too verb; "remove" forgets
        // the torrent and leaves the payload where it is (#287).
        //
        // `forget` takes the same verb as `delete-with-data`, stated rather than
        // shared by accident: uTorrent holds no record of a torrent it has
        // removed, so there is nothing left to clear afterwards.
        var verb = action switch
        {
            DownloadClientActions.Pause => "pause",
            DownloadClientActions.Resume => "start",
            DownloadClientActions.Recheck => "recheck",
            DownloadClientActions.Delete => "remove",
            DownloadClientActions.DeleteWithData => "removedata",
            DownloadClientActions.Forget => "removedata",
            _ => null
        };
        if (verb is null) return DownloadClientHelpers.Unsupported(client, queueItemId, action, "uTorrent");
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.MissingAddress(client, queueItemId, action);
        using var http = CreateHttp(client, baseUri, TimeSpan.FromSeconds(8));
        var token = await DownloadClientHelpers.GetUTorrentTokenAsync(http, cancellationToken);
        using var response = await http.GetAsync($"gui/?token={Uri.EscapeDataString(token)}&action={verb}&hash={Uri.EscapeDataString(queueItemId)}", cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return DownloadClientHelpers.ActionSuccess(client, queueItemId, action, "uTorrent action sent.");
        }

        return DownloadClientHelpers.ActionFailure(
            client,
            queueItemId,
            action,
            $"uTorrent returned {(int)response.StatusCode}.",
            response.StatusCode);
    }

    /// <summary>
    /// This is the one place uTorrent's status logic lives — <c>GetSnapshotAsync</c>
    /// calls through here rather than reimplementing the decode, so this
    /// override is no longer dead code (#728).
    ///
    /// <para>uTorrent has two unrelated notions of "native status" in play,
    /// and this override answers both without breaking either caller. The
    /// WebUI `list` response has no status text at all — only the bitfield —
    /// so <c>GetSnapshotAsync</c> passes it here as its decimal string form.
    /// Everything else that calls <see cref="IDownloadClient.NormalizeStatus"/>
    /// generically across every client (<c>DownloadClientTelemetryProfilesTests</c>)
    /// passes keyword text such as "Error"/"Queued"/"Seeding", the same shape
    /// Deluge uses, so a non-numeric value still falls back to
    /// <see cref="DownloadClientHelpers.NormalizeTextStatus"/> rather than
    /// losing that contract.</para>
    /// </summary>
    public override string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
        => !string.IsNullOrWhiteSpace(nativeStatus) && int.TryParse(nativeStatus, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits)
            ? DecodeStatusBitfield(bits, progress ?? 0)
            : DownloadClientHelpers.NormalizeTextStatus(nativeStatus, progress);

    /// <summary>
    /// Decodes the WebUI `list` status bitfield together with progress into
    /// one of <see cref="DownloadQueueStatuses"/> (#728).
    /// </summary>
    private static string DecodeStatusBitfield(int bits, double progress)
    {
        // An errored torrent outranks everything else, the same way Deluge's
        // "error" text does (DownloadClientHelpers.NormalizeTextStatus): a
        // torrent sitting at 100% with the error bit set is not ready to
        // import, it is broken.
        if ((bits & UTorrentStatus.Error) != 0) return DownloadQueueStatuses.Stalled;
        // Checked ahead of the progress-based ImportReady test (#912): a
        // torrent already at 100% before uTorrent re-checks it (a restart, a
        // moved data folder) still carries the Checking bit while it verifies,
        // and reading progress first raced the import against a file uTorrent
        // was still hashing.
        if ((bits & UTorrentStatus.Checking) != 0) return DownloadQueueStatuses.Downloading;
        if (Deluno.Platform.Contracts.DownloadHealthStrikes.IsFinishedPercent(progress)) return DownloadQueueStatuses.ImportReady;
        if ((bits & (UTorrentStatus.Paused | UTorrentStatus.Queued)) != 0) return DownloadQueueStatuses.Queued;
        if ((bits & UTorrentStatus.Started) != 0) return DownloadQueueStatuses.Downloading;
        return DownloadQueueStatuses.Queued;
    }

    /// <summary>
    /// The files inside one torrent (#816/#944), read with the WebUI's own
    /// <c>action=getfiles</c> call. Its reply is positional and heterogeneous
    /// - <c>files: [hash, [[name, size, downloaded, priority], ...]]</c> -
    /// unlike the flat, named-field replies qBittorrent/Transmission/Deluge
    /// return, so the second element is deserialized separately rather than
    /// typed directly on <see cref="UTorrentGetFilesResponse"/>.
    /// </summary>
    public override async Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null || string.IsNullOrWhiteSpace(queueItemId)) return null;
        using var http = CreateHttp(client, baseUri, TimeSpan.FromSeconds(8));
        try
        {
            var token = await DownloadClientHelpers.GetUTorrentTokenAsync(http, cancellationToken);
            var payload = await http.GetFromJsonAsync<UTorrentGetFilesResponse>(
                $"gui/?token={Uri.EscapeDataString(token)}&action=getfiles&hash={Uri.EscapeDataString(queueItemId)}", cancellationToken);
            if (payload?.Files is not { Count: > 1 } files) return null;

            var entries = files[1].Deserialize<List<List<JsonElement>>>() ?? [];
            return entries
                .Where(entry => entry.Count > 0)
                .Select(entry => AsString(entry[0]))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    private HttpClient CreateHttp(DownloadClientItem client, Uri baseUri, TimeSpan timeout)
        => new(handler(client), disposeHandler: handlerFactory is null) { BaseAddress = baseUri, Timeout = timeout };

    /// <summary>
    /// The infohashes uTorrent is holding, or null when it will not say. Null
    /// rather than empty on purpose, the same distinction qBittorrent's own
    /// hash diff makes (#606): "I could not read the list" and "the list is
    /// empty" lead to opposite conclusions about whether an add worked.
    /// </summary>
    private static async Task<HashSet<string>?> ListHashesAsync(HttpClient http, string token, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await http.GetFromJsonAsync<UTorrentListResponse>($"gui/?token={Uri.EscapeDataString(token)}&list=1", cancellationToken);
            return (payload?.Torrents ?? [])
                .Where(item => item.Count > 0)
                .Select(item => AsString(item[0]))
                .Where(hash => !string.IsNullOrWhiteSpace(hash))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Bounded the same way qBittorrent's grab waits: a person is watching a
    /// page for the answer, so this polls briefly rather than hanging.
    /// </summary>
    /// <summary>
    /// uTorrent's own spelling of a hash once it shows in its list: the known one
    /// when Deluno has one, else the single torrent that appeared. Null when it
    /// never shows within the window (or, unknown, when more than one appeared).
    /// </summary>
    private static async Task<string?> WaitForHashAsync(HttpClient http, string token, HashSet<string>? before, string? knownHash, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(6);
        while (true)
        {
            var after = await ListHashesAsync(http, token, cancellationToken);
            if (knownHash is not null)
            {
                var found = after?.FirstOrDefault(hash => string.Equals(hash, knownHash, StringComparison.OrdinalIgnoreCase));
                if (found is not null) return found;
            }
            else if (before is not null && after is not null && after.Except(before).Any())
            {
                return DownloadClientHelpers.TheOnlyNewItem(before, after);
            }

            if (DateTimeOffset.UtcNow >= deadline) return null;
            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }
    }

    /// <summary>
    /// Elapsed seeding time has no index in the `list` array at all — it
    /// requires a separate `getprops` call (#728, same blindness #661
    /// documents for Transmission/Deluge). Batched into one call across every
    /// hash in the snapshot rather than one request per torrent.
    /// </summary>
    private static async Task<Dictionary<string, int>> GetSeedingMinutesAsync(
        HttpClient http,
        string token,
        IReadOnlyList<IReadOnlyList<JsonElement>> torrents,
        CancellationToken cancellationToken)
    {
        var hashes = torrents
            .Where(item => item.Count > 0)
            .Select(item => AsString(item[0]))
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .ToArray();
        if (hashes.Length == 0) return [];

        var hashQuery = string.Join("&", hashes.Select(hash => $"hash={Uri.EscapeDataString(hash)}"));
        try
        {
            var response = await http.GetFromJsonAsync<UTorrentGetPropsResponse>(
                $"gui/?token={Uri.EscapeDataString(token)}&action=getprops&{hashQuery}", cancellationToken);
            return (response?.Props ?? [])
                .Where(prop => !string.IsNullOrWhiteSpace(prop.Hash))
                .GroupBy(prop => prop.Hash!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (int)Math.Clamp((group.First().SeedingSeconds ?? 0) / 60, 0, int.MaxValue),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return [];
        }
    }

    private static string AsString(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    private static double AsDouble(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed) ? parsed : double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
    private static long AsInt64(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed) ? parsed : long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
    private sealed record UTorrentListResponse([property: JsonPropertyName("torrents")] IReadOnlyList<IReadOnlyList<JsonElement>>? Torrents);
    private sealed record UTorrentGetFilesResponse([property: JsonPropertyName("files")] IReadOnlyList<JsonElement>? Files);
    private sealed record UTorrentGetPropsResponse([property: JsonPropertyName("props")] IReadOnlyList<UTorrentProps>? Props);
    private sealed record UTorrentProps(
        [property: JsonPropertyName("hash")] string? Hash,
        [property: JsonPropertyName("seed_time")] long? SeedingSeconds);
}
