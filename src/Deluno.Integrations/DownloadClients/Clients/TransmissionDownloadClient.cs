using System.Globalization;
using Deluno.Infrastructure.Http;
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
        // A .torrent Deluno fetched goes as metainfo; a URL or magnet as a
        // filename. The label is the category, which is how the queue and
        // import tell a film from an episode (#606).
        var arguments = new Dictionary<string, object> { ["paused"] = client.AddPaused };
        if (request.TorrentFile is { } file)
        {
            arguments["metainfo"] = Convert.ToBase64String(file);
        }
        else
        {
            arguments["filename"] = request.DownloadUrl;
        }

        if (DownloadClientHelpers.ResolveCategory(client, request) is { Length: > 0 } label)
        {
            arguments["labels"] = new[] { label };

            // Transmission has no folder per label, so a label alone put films and
            // TV in one folder that no processor watched (#629). Each grab names
            // its own: Transmission's download folder plus the category, the way
            // Radarr and Sonarr do it.
            var session = await SendAsync(client, baseUri, new TransmissionRequest("session-get", new() { ["fields"] = new[] { "download-dir" } }), cancellationToken);
            if (CategoryFolder(session.Arguments?.DownloadDir, label) is { } folder)
            {
                arguments["download-dir"] = folder;
            }
        }

        var response = await SendAsync(client, baseUri, new TransmissionRequest("torrent-add", arguments), cancellationToken);
        // The legacy protocol answers "success" or a reason in words; only a
        // reason is a refusal.
        if (response.Result is { ValueKind: System.Text.Json.JsonValueKind.String } result
            && !string.Equals(result.GetString(), "success", StringComparison.OrdinalIgnoreCase))
        {
            return DownloadClientHelpers.GrabFailure(client, request, $"Transmission did not add the release: {result.GetString()}.");
        }

        // Its id, as the queue reports it, so the import can find this grab by it (#617).
        var added = response.Arguments?.Added ?? response.Arguments?.Duplicate;
        return DownloadClientHelpers.GrabSuccess(client, request, request.TorrentFile is null ? "Release URL sent to Transmission." : "Torrent file sent to Transmission.") with
        {
            ExternalId = added?.Id?.ToString(CultureInfo.InvariantCulture)
        };
    }

    public override async Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return CreateConfigurationSnapshot(client, capturedUtc, "Download client address is missing.");
        var response = await SendAsync(client, baseUri, new TransmissionRequest("torrent-get", new()
        {
            // uploadRatio/secondsSeeding: #661 — without them Ratio/SeedingMinutes
            // stayed null forever and a sharing rule could never be seen as met.
            // isStalled: Transmission's own, better-informed stall detection,
            // folded into NormalizeStatus below rather than reinventing a
            // cruder one. peersSendingToUs is requested and deserialized too
            // (#661), but deliberately not used for Peers below: it is 0 for
            // a magnet still exchanging metadata over a connected peer, and
            // ReconcileTorrentStatus's own no-metadata-no-peers stall window
            // reads Peers as "is there anyone to talk to at all" — switching
            // it to sending-only would stall that magnet on the first poll.
            ["fields"] = new[] { "id", "name", "status", "percentDone", "rateDownload", "rateUpload", "eta", "totalSize", "downloadedEver", "peersConnected", "peersSendingToUs", "addedDate", "doneDate", "downloadDir", "labels", "error", "errorString", "isStalled", "uploadRatio", "secondsSeeding", "isPrivate" }
        }), cancellationToken);
        var queue = (response.Arguments?.Torrents ?? []).Select(item => new DownloadQueueItem(
            item.Id?.ToString(CultureInfo.InvariantCulture) ?? item.Name ?? Guid.CreateVersion7().ToString("N"), client.Id, client.Name, client.Protocol,
            DownloadClientHelpers.InferMediaType(client, item.Labels?.FirstOrDefault()), DownloadClientHelpers.CleanReleaseTitle(item.Name ?? "Unknown Transmission item"),
            item.Name ?? "Unknown Transmission item", item.Labels?.FirstOrDefault() ?? string.Empty, Status(item),
            Math.Clamp(Math.Round((item.PercentDone ?? 0) * 100, 1), 0, 100), Math.Round((item.RateDownload ?? 0) / 1_000_000d, 1), Math.Max(0, item.Eta ?? 0),
            item.TotalSize ?? 0, item.DownloadedEver ?? 0, item.PeersConnected ?? 0, "Transmission", string.IsNullOrWhiteSpace(item.ErrorString) ? null : item.ErrorString,
            DownloadClientHelpers.FromUnix(item.AddedDate), DownloadClientHelpers.ResolveDownloadPath(item.DownloadDir, item.Name),
            // Both come back on the same torrent-get call, so a sharing rule
            // costs no extra request. secondsSeeding is 0 while still downloading.
            Ratio: item.UploadRatio,
            SeedingMinutes: item.SecondsSeeding is null ? null : (int)Math.Clamp(item.SecondsSeeding.Value / 60, 0, int.MaxValue),
            UploadSpeedMbps: Math.Round((item.RateUpload ?? 0) / 1_000_000d, 1),
            // #880/#881: Transmission's own error field distinguishes a local
            // problem from a tracker one. TR_STAT_LOCAL_ERROR (3) is
            // Transmission's word for a problem on this machine — disk space,
            // permissions, a missing file — while TR_STAT_TRACKER_WARNING (1)
            // and TR_STAT_TRACKER_ERROR (2) are the tracker refusing or
            // complaining about the release itself, which stays a release-side
            // finding.
            IsSetupProblem: item.Error == TrLocalError,
            IsPrivate: item.IsPrivate))
            .Select(item => DownloadClientHelpers.ReconcileTorrentStatus(item, capturedUtc))
            .ToArray();
        var freeSpaceBytes = await GetFreeSpaceBytesAsync(client, baseUri, cancellationToken);
        var (health, message) = DownloadClientHelpers.EvaluateDiskSpaceHealth(
            $"Connected to Transmission at {baseUri.Host}:{baseUri.Port}.",
            freeSpaceBytes,
            null);
        return CreateSnapshot(client, queue, capturedUtc, health, message, freeSpaceBytes: freeSpaceBytes);
    }

    /// <summary>
    /// Transmission's <c>free-space</c> RPC method, asked about its own
    /// download folder (#909). A daemon too old to know the method, or one
    /// with nothing configured to ask about, reports nothing rather than a
    /// guessed number.
    /// </summary>
    private async Task<long?> GetFreeSpaceBytesAsync(DownloadClientItem client, Uri baseUri, CancellationToken cancellationToken)
    {
        try
        {
            var session = await SendAsync(client, baseUri, new TransmissionRequest("session-get", new() { ["fields"] = new[] { "download-dir" } }), cancellationToken);
            var downloadDir = session.Arguments?.DownloadDir;
            if (string.IsNullOrWhiteSpace(downloadDir))
            {
                return null;
            }

            var freeSpace = await SendAsync(client, baseUri, new TransmissionRequest("free-space", new() { ["path"] = downloadDir }), cancellationToken);
            return freeSpace.Arguments?.SizeBytes;
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Transmission's own stall detection folded in on top of the status-code
    /// mapping (#661): it watches real peer/tracker activity Deluno does not
    /// see, so a torrent it has already flagged stalled should read as
    /// <see cref="DownloadQueueStatuses.Stalled"/> immediately rather than
    /// waiting on the generic no-throughput health check to notice on its own.
    /// Only promotes a torrent still reading as Downloading — an error, a
    /// completion or a pause already outranks "stalled".
    /// </summary>
    private string Status(TransmissionTorrent item)
    {
        var status = NormalizeStatus(item.Status?.ToString(CultureInfo.InvariantCulture), item.PercentDone, item.Error, item.ErrorString);
        return item.IsStalled == true && status == DownloadQueueStatuses.Downloading
            ? DownloadQueueStatuses.Stalled
            : status;
    }

    public override async Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
    {
        // One arm per verb, with `delete-local-data` set by the arm rather than
        // recomputed, because that flag is the whole of the difference between
        // two of them. Deluno never deletes a shared file itself (#287).
        //
        // `forget` is the same request as `delete-with-data`, said rather than
        // implied: Transmission keeps no record of a torrent it no longer has,
        // so removing it is the whole of forgetting it.
        var (method, deleteLocalData) = action switch
        {
            DownloadClientActions.Pause => ("torrent-stop", (bool?)null),
            DownloadClientActions.Resume => ("torrent-start", null),
            DownloadClientActions.Recheck => ("torrent-verify", null),
            DownloadClientActions.Delete => ("torrent-remove", false),
            DownloadClientActions.DeleteWithData => ("torrent-remove", true),
            DownloadClientActions.Forget => ("torrent-remove", true),
            _ => (null, null)
        };
        if (method is null) return DownloadClientHelpers.Unsupported(client, queueItemId, action, "Transmission");
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.MissingAddress(client, queueItemId, action);
        if (!DownloadClientHelpers.TryParseId(queueItemId, out var torrentId))
        {
            return DownloadClientHelpers.UnreadableId(client, queueItemId, action, "Transmission");
        }

        var arguments = new Dictionary<string, object> { ["ids"] = new[] { torrentId } };
        if (deleteLocalData is { } wipe) arguments["delete-local-data"] = wipe;
        await SendAsync(client, baseUri, new TransmissionRequest(method, arguments), cancellationToken);
        return DownloadClientHelpers.ActionSuccess(client, queueItemId, action, "Transmission action sent.");
    }

    /// <summary>
    /// Transmission has no categories of its own. Deluno names each grab's
    /// folder (#629), so the folder a category saves to is Transmission's
    /// download folder plus the category (#616).
    /// </summary>
    public override async Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken)
    {
        var name = category.Trim();
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (name.Length == 0 || baseUri is null)
        {
            return new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Configuration,
                name.Length == 0 ? "Enter a category before checking it." : "Add the Transmission address before checking a category.", Supported: true, Found: false);
        }

        try
        {
            var session = await SendAsync(client, baseUri, new TransmissionRequest("session-get", new() { ["fields"] = new[] { "download-dir" } }), cancellationToken);
            var folder = CategoryFolder(session.Arguments?.DownloadDir, name);
            return folder is null
                ? new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Configuration,
                    "Transmission did not say where it saves downloads.", Supported: true, Found: false)
                : new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Ready,
                    $"Transmission has no categories of its own; Deluno sends each {name} download to {folder}, inside Transmission's download folder.",
                    Supported: true, Found: true, SavePath: folder);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            return new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Unreachable,
                $"Deluno could not read Transmission's download folder: {ex.Message}", Supported: true, Found: false);
        }
    }

    /// <summary>
    /// Transmission's <c>error</c> field: 0 none, 1 tracker warning, 2 tracker
    /// error, 3 local error (#880, #881) — a problem on this machine, such as
    /// disk space or permissions, that Transmission distinguishes from
    /// anything the tracker said about the release.
    /// </summary>
    internal const int TrLocalError = 3;

    public override string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
    {
        if (errorCode is > 0 || !string.IsNullOrWhiteSpace(errorMessage)) return DownloadQueueStatuses.Stalled;
        var parsed = int.TryParse(nativeStatus, out var status);

        // TR_STATUS_CHECK (2): Transmission is hash-verifying local data. A
        // torrent that was already fully downloaded before a recheck reports
        // percentDone at 1 throughout the check, so the progress-based
        // ImportReady test below must never see this case — otherwise a
        // re-verify races the import against a file Transmission might still
        // rewrite (#912).
        if (parsed && status == 2) return DownloadQueueStatuses.NotTransferring;

        if ((progress ?? 0) >= 1) return DownloadQueueStatuses.ImportReady;
        if (!parsed) return DownloadQueueStatuses.Queued;
        return status switch
        {
            4 => DownloadQueueStatuses.Downloading,
            // Status 0 is stopped: a person stopped this, incomplete, download
            // by hand. Distinct from the queued-behind-others statuses (1, 3,
            // 5), which used to collapse into the same word (#661).
            0 => DownloadQueueStatuses.Paused,
            _ => DownloadQueueStatuses.Queued
        };
    }

    private async Task<TransmissionResponse> SendAsync(DownloadClientItem client, Uri baseUri, TransmissionRequest payload, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);
        DownloadClientHelpers.AddBasicAuth(http, client);
        var uri = new Uri(baseUri, "transmission/rpc");
        using var first = await http.PostSizedJsonAsync(uri, payload, cancellationToken);
        if ((int)first.StatusCode == 409 && first.Headers.TryGetValues("X-Transmission-Session-Id", out var values))
        {
            http.DefaultRequestHeaders.Remove("X-Transmission-Session-Id");
            http.DefaultRequestHeaders.Add("X-Transmission-Session-Id", values.First());
            using var second = await http.PostSizedJsonAsync(uri, payload, cancellationToken);
            second.EnsureSuccessStatusCode();
            return await second.Content.ReadFromJsonAsync<TransmissionResponse>(cancellationToken) ?? new(null);
        }
        first.EnsureSuccessStatusCode();
        return await first.Content.ReadFromJsonAsync<TransmissionResponse>(cancellationToken) ?? new(null);
    }

    /// <summary>
    /// The category's folder inside Transmission's download folder, written the
    /// way that folder is written: a Windows path for a Windows daemon, a POSIX
    /// one for a Linux or Docker one. Null with no download folder to build on.
    /// </summary>
    public static string? CategoryFolder(string? downloadDir, string category)
    {
        if (string.IsNullOrWhiteSpace(downloadDir) || string.IsNullOrWhiteSpace(category))
        {
            return null;
        }

        var posix = downloadDir.Contains('/') && !downloadDir.Contains('\\');
        var separator = posix ? '/' : '\\';
        var trimmed = downloadDir.TrimEnd('/', '\\');
        var last = trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
        // Already the category's folder: someone pointed Transmission at it.
        return string.Equals(last, category, StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + separator + category;
    }

    private sealed record TransmissionRequest([property: JsonPropertyName("method")] string Method, [property: JsonPropertyName("arguments")] Dictionary<string, object> Arguments);
    private sealed record TransmissionResponse([property: JsonPropertyName("arguments")] TransmissionArguments? Arguments, [property: JsonPropertyName("result")] System.Text.Json.JsonElement? Result = null);
    private sealed record TransmissionArguments(
        [property: JsonPropertyName("torrents")] IReadOnlyList<TransmissionTorrent>? Torrents,
        [property: JsonPropertyName("torrent-added")] TransmissionAdded? Added = null,
        [property: JsonPropertyName("torrent-duplicate")] TransmissionAdded? Duplicate = null,
        [property: JsonPropertyName("download-dir")] string? DownloadDir = null,
        [property: JsonPropertyName("size-bytes")] long? SizeBytes = null);
    private sealed record TransmissionAdded([property: JsonPropertyName("id")] int? Id, [property: JsonPropertyName("hashString")] string? HashString);
    private sealed record TransmissionTorrent([property: JsonPropertyName("id")] int? Id, [property: JsonPropertyName("name")] string? Name, [property: JsonPropertyName("status")] int? Status, [property: JsonPropertyName("percentDone")] double? PercentDone, [property: JsonPropertyName("rateDownload")] long? RateDownload, [property: JsonPropertyName("rateUpload")] long? RateUpload, [property: JsonPropertyName("eta")] int? Eta, [property: JsonPropertyName("totalSize")] long? TotalSize, [property: JsonPropertyName("downloadedEver")] long? DownloadedEver, [property: JsonPropertyName("peersConnected")] int? PeersConnected, [property: JsonPropertyName("addedDate")] long? AddedDate, [property: JsonPropertyName("downloadDir")] string? DownloadDir, [property: JsonPropertyName("labels")] IReadOnlyList<string>? Labels, [property: JsonPropertyName("error")] int? Error, [property: JsonPropertyName("errorString")] string? ErrorString, [property: JsonPropertyName("peersSendingToUs")] int? PeersSendingToUs = null, [property: JsonPropertyName("isStalled")] bool? IsStalled = null, [property: JsonPropertyName("uploadRatio")] double? UploadRatio = null, [property: JsonPropertyName("secondsSeeding")] long? SecondsSeeding = null, [property: JsonPropertyName("isPrivate")] bool? IsPrivate = null);
}
