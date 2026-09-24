using System.Net.Http.Json;
using System.Text.Json;
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
        // Deluge has a call for each kind of release: add_torrent_url fetches a
        // .torrent itself and cannot take a magnet, so a magnet sent there was
        // lost (#606).
        var options = new Dictionary<string, object>();
        var add = request.TorrentFile is { } file
            ? new DelugeRequest("core.add_torrent_file", [SafeFileName(request.ReleaseName) + ".torrent", Convert.ToBase64String(file), options], 2)
            : request.DownloadUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                ? new DelugeRequest("core.add_torrent_magnet", [request.DownloadUrl, options], 2)
                : new DelugeRequest("core.add_torrent_url", [request.DownloadUrl, options], 2);
        var added = await DownloadClientHelpers.PostJsonAsync<DelugeResponse<string>>(http, new Uri(baseUri, "json"), add, cancellationToken);
        if (string.IsNullOrWhiteSpace(added?.Result))
        {
            return DownloadClientHelpers.GrabFailure(client, request, $"Deluge did not add the release: {added?.Error?.Message ?? "it may already hold it"}.");
        }

        await TryLabelAsync(http, baseUri, added.Result, DownloadClientHelpers.ResolveCategory(client, request), cancellationToken);
        // The torrent id Deluge returns is its hash and its queue id, so the
        // import can find this grab by it (#617).
        return DownloadClientHelpers.GrabSuccess(client, request, request.TorrentFile is null ? "Release sent to Deluge." : "Torrent file sent to Deluge.") with { ExternalId = added.Result };
    }

    public override async Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return CreateConfigurationSnapshot(client, capturedUtc, "Download client address is missing.");
        var http = CreateHttp();
        await LoginAsync(http, baseUri, client, cancellationToken);
        var payload = new DelugeRequest("web.update_ui", [new[] { "name", "state", "progress", "download_payload_rate", "upload_payload_rate", "eta", "total_size", "total_done", "num_peers", "time_added", "label", "message", "save_path", "move_completed", "move_completed_path" }, new Dictionary<string, object>()], 2);
        var response = await DownloadClientHelpers.PostJsonAsync<DelugeResponse<DelugeUpdateResult>>(http, new Uri(baseUri, "json"), payload, cancellationToken);
        var queue = (response?.Result?.Torrents ?? new Dictionary<string, DelugeTorrent>()).Select(pair =>
        {
            var item = pair.Value;
            return new DownloadQueueItem(pair.Key, client.Id, client.Name, client.Protocol, DownloadClientHelpers.InferMediaType(client, item.Label),
                DownloadClientHelpers.CleanReleaseTitle(item.Name ?? "Unknown Deluge item"), item.Name ?? "Unknown Deluge item", item.Label ?? string.Empty,
                StillMoving(item) ? DownloadQueueStatuses.Downloading : NormalizeStatus(item.State, item.Progress), Math.Clamp(Math.Round(item.Progress ?? 0, 1), 0, 100), Math.Round((item.DownloadPayloadRate ?? 0) / 1_000_000d, 1),
                Math.Max(0, Convert.ToInt32(item.Eta ?? 0)), Convert.ToInt64(item.TotalSize ?? 0), Convert.ToInt64(item.TotalDone ?? 0), item.NumPeers ?? 0, "Deluge",
                DelugeMessage(item.Message), DownloadClientHelpers.FromUnix(Convert.ToInt64(item.TimeAdded ?? 0)), DownloadClientHelpers.ResolveDownloadPath(item.SavePath, item.Name),
                UploadSpeedMbps: Math.Round((item.UploadPayloadRate ?? 0) / 1_000_000d, 1));
        }).ToArray();
        return CreateSnapshot(client, queue, capturedUtc, "healthy", $"Connected to Deluge at {baseUri.Host}:{baseUri.Port}.");
    }

    public override async Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
    {
        // One arm per verb. `remove_data` is the second argument to
        // core.remove_torrent and is the only thing separating two of these, so
        // it is set by the arm rather than recomputed from the action name.
        //
        // `forget` is deliberately the same request as `delete-with-data` here,
        // and that is a statement rather than a fall-through: Deluge keeps no
        // record of a torrent it no longer holds, so it refuses a release only
        // while the transfer is still there. Once it is gone the client accepts
        // the release again and there is nothing further to clear. The usenet
        // clients are the ones that need a second request.
        var (method, removeData) = action switch
        {
            DownloadClientActions.Pause => ("core.pause_torrent", (bool?)null),
            DownloadClientActions.Resume => ("core.resume_torrent", null),
            DownloadClientActions.Recheck => ("core.force_recheck", null),
            DownloadClientActions.Delete => ("core.remove_torrent", false),
            DownloadClientActions.DeleteWithData => ("core.remove_torrent", true),
            DownloadClientActions.Forget => ("core.remove_torrent", true),
            _ => (null, null)
        };
        if (method is null) return DownloadClientHelpers.Unsupported(client, queueItemId, action, "Deluge");
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.MissingAddress(client, queueItemId, action);
        var http = CreateHttp();
        await LoginAsync(http, baseUri, client, cancellationToken);
        object[] parameters = removeData is { } wipe
            ? [new[] { queueItemId }, wipe]
            : [new[] { queueItemId }];
        await DownloadClientHelpers.PostJsonAsync<DelugeResponse<object>>(http, new Uri(baseUri, "json"), new DelugeRequest(method, parameters, 3), cancellationToken);
        return DownloadClientHelpers.ActionSuccess(client, queueItemId, action, "Deluge action sent.");
    }

    /// <summary>
    /// Finished, but not yet where Deluge will leave it. With "move completed"
    /// on, Deluge reaches 100% in its incomplete folder and moves the files
    /// afterwards; Deluno handed a processor the incomplete path in that gap,
    /// and Weir refused it (#606 live test).
    /// </summary>
    /// <summary>
    /// Deluge's message for a healthy torrent is "OK". Passed on as it was, every
    /// Deluge download showed "queue failed: OK" (#606 live test).
    /// </summary>
    private static string? DelugeMessage(string? message)
        => string.IsNullOrWhiteSpace(message) || string.Equals(message.Trim(), "OK", StringComparison.OrdinalIgnoreCase) ? null : message;

    private static bool StillMoving(DelugeTorrent item)
        => string.Equals(item.State, "Moving", StringComparison.OrdinalIgnoreCase)
           || item.Progress >= 100 && item.MoveCompleted == true && !string.IsNullOrWhiteSpace(item.MoveCompletedPath)
              // Deluge's own paths, whichever system it runs on: trimmed of either
              // separator, not by this host's rules (CI runs Linux, Deluge Windows).
              && !string.Equals((item.SavePath ?? string.Empty).TrimEnd('\\', '/'), item.MoveCompletedPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    public override string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
        => DownloadClientHelpers.NormalizeTextStatus(nativeStatus, progress);

    private HttpClient CreateHttp()
    {
        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);
        return http;
    }

    /// <summary>
    /// Signs in to the web UI and makes sure it is connected to a daemon. An
    /// empty password is still a sign-in: skipping it left every later call
    /// unauthenticated. And the web UI answers with no daemon behind it, so a
    /// Deluge that is up but not connected took nothing (#606).
    /// </summary>
    private static async Task LoginAsync(HttpClient http, Uri baseUri, DownloadClientItem client, CancellationToken cancellationToken)
    {
        var json = new Uri(baseUri, "json");
        var login = await DownloadClientHelpers.PostJsonAsync<DelugeResponse<bool>>(http, json, new DelugeRequest("auth.login", [client.Secret ?? string.Empty], 1), cancellationToken);
        if (login?.Result != true)
        {
            throw new InvalidOperationException("Deluge refused the password.");
        }

        var connected = await DownloadClientHelpers.PostJsonAsync<DelugeResponse<bool>>(http, json, new DelugeRequest("web.connected", [], 2), cancellationToken);
        if (connected?.Result == true)
        {
            return;
        }

        var hosts = await DownloadClientHelpers.PostJsonAsync<DelugeResponse<JsonElement>>(http, json, new DelugeRequest("web.get_hosts", [], 3), cancellationToken);
        if (hosts?.Result is not { ValueKind: JsonValueKind.Array } list || list.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Deluge's web UI has no daemon to connect to.");
        }

        var hostId = list[0][0].GetString()!;
        await DownloadClientHelpers.PostJsonAsync<DelugeResponse<object>>(http, json, new DelugeRequest("web.connect", [hostId], 4), cancellationToken);
    }

    /// <summary>The category as a label, when the Label plugin is on; a Deluge without it still downloads.</summary>
    private static async Task TryLabelAsync(HttpClient http, Uri baseUri, string torrentId, string category, CancellationToken cancellationToken)
    {
        var label = category.Trim().ToLowerInvariant();
        if (label.Length == 0)
        {
            return;
        }

        var json = new Uri(baseUri, "json");
        try
        {
            var labels = await DownloadClientHelpers.PostJsonAsync<DelugeResponse<string[]>>(http, json, new DelugeRequest("label.get_labels", [], 5), cancellationToken);
            if (labels?.Result is null)
            {
                return;
            }

            if (!labels.Result.Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                await DownloadClientHelpers.PostJsonAsync<DelugeResponse<object>>(http, json, new DelugeRequest("label.add", [label], 6), cancellationToken);
            }

            await DownloadClientHelpers.PostJsonAsync<DelugeResponse<object>>(http, json, new DelugeRequest("label.set_torrent", [torrentId, label], 7), cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or NotSupportedException)
        {
            // No Label plugin: the torrent is added, just unlabelled.
        }
    }

    private static string SafeFileName(string name)
        => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private sealed record DelugeRequest([property: JsonPropertyName("method")] string Method, [property: JsonPropertyName("params")] object[] Params, [property: JsonPropertyName("id")] int Id);
    private sealed record DelugeResponse<T>([property: JsonPropertyName("result")] T? Result, [property: JsonPropertyName("error")] DelugeError? Error = null);
    private sealed record DelugeError([property: JsonPropertyName("message")] string? Message);
    private sealed record DelugeUpdateResult([property: JsonPropertyName("torrents")] IReadOnlyDictionary<string, DelugeTorrent>? Torrents);
    private sealed record DelugeTorrent([property: JsonPropertyName("name")] string? Name, [property: JsonPropertyName("state")] string? State, [property: JsonPropertyName("progress")] double? Progress, [property: JsonPropertyName("download_payload_rate")] double? DownloadPayloadRate, [property: JsonPropertyName("upload_payload_rate")] double? UploadPayloadRate, [property: JsonPropertyName("eta")] double? Eta, [property: JsonPropertyName("total_size")] double? TotalSize, [property: JsonPropertyName("total_done")] double? TotalDone, [property: JsonPropertyName("num_peers")] int? NumPeers, [property: JsonPropertyName("time_added")] double? TimeAdded, [property: JsonPropertyName("label")] string? Label, [property: JsonPropertyName("message")] string? Message, [property: JsonPropertyName("save_path")] string? SavePath, [property: JsonPropertyName("move_completed")] bool? MoveCompleted = null, [property: JsonPropertyName("move_completed_path")] string? MoveCompletedPath = null);
}
