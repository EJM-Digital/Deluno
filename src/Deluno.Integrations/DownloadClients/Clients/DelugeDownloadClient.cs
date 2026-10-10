using Deluno.Infrastructure.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Connections.Contracts;
using Deluno.Contracts;

namespace Deluno.Integrations.DownloadClients.Clients;

public sealed class DelugeDownloadClient(IHttpClientFactory httpClientFactory) : DownloadClientBase
{
    public override string Protocol => "deluge";
    public override DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, false, true, true, true, true, "password", SupportsQueueReorder: true);

    public override bool SetsSaveFolderPerGrab => true;

    public override async Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.GrabFailure(client, request, "Download client address is missing.");
        var http = CreateHttp();
        await LoginAsync(http, baseUri, client, cancellationToken);
        // Deluge has a call for each kind of release: add_torrent_url fetches a
        // .torrent itself and cannot take a magnet, so a magnet sent there was
        // lost (#606).
        var options = new Dictionary<string, object> { ["add_paused"] = client.AddPaused };
        // #1073/#1164: Deluge's label has no folder unless someone set one by hand,
        // so the library's downloads folder is named as where the torrent ENDS UP
        // (move_completed_path). Where it downloads in the meantime is left to
        // Deluge's own download_location, its incomplete folder: Deluno never
        // sends download_location, so half-finished files stay out of the
        // folder the processor and media servers watch.
        if (!string.IsNullOrWhiteSpace(request.SaveFolder))
        {
            options["move_completed"] = true;
            options["move_completed_path"] = request.SaveFolder;
        }

        var add = request.TorrentFile is { } file
            ? new DelugeRequest("core.add_torrent_file", [SafeFileName(request.ReleaseName) + ".torrent", Convert.ToBase64String(file), options], 2)
            : request.DownloadUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                ? new DelugeRequest("core.add_torrent_magnet", [request.DownloadUrl, options], 2)
                : new DelugeRequest("core.add_torrent_url", [request.DownloadUrl, options], 2);
        var added = await DownloadClientHelpers.PostJsonAsync<DelugeResponse<string>>(http, new Uri(baseUri, "json"), add, cancellationToken);
        if (string.IsNullOrWhiteSpace(added?.Result))
        {
            // #877: Deluge's add calls answer with a null result both when the
            // release is malformed and when it already holds this exact
            // torrent — nothing in the RPC tells the two apart. A magnet link
            // names its own hash, so it can be checked directly against
            // Deluge's own session state instead of failing outright the way
            // a torrent client used to (cleanup sheet B3; Usenet clients
            // already follow the existing item).
            var knownHash = DownloadClientHelpers.KnownHashOf(request);
            if (knownHash is { Length: 40 } duplicateHash)
            {
                var session = await DownloadClientHelpers.PostJsonAsync<DelugeResponse<string[]>>(
                    http, new Uri(baseUri, "json"), new DelugeRequest("core.get_session_state", [], 9), cancellationToken);
                if (session?.Result?.Contains(duplicateHash, StringComparer.OrdinalIgnoreCase) == true)
                {
                    return DownloadClientHelpers.GrabSuccess(
                        client,
                        request,
                        "Deluge already holds this release. Deluno is following the copy it has instead of sending another.") with
                    {
                        ExternalId = duplicateHash,
                        IsDuplicateFollow = true
                    };
                }
            }

            // #874/#942, cleanup sheet B2: an explicit RPC error object is
            // Deluge looking at the exact link or file it was handed and
            // refusing it outright, in the same request — a dead link or a
            // torrent it could not parse — distinct from the ambiguous null
            // result above, which is just as likely an already-held
            // duplicate Deluno could not verify. Read as a dead release only
            // when Deluge actually said why.
            if (added?.Error?.Message is { Length: > 0 } errorMessage)
            {
                return DownloadClientHelpers.DeadReleaseGrabFailure(client, request, $"Deluge refused the release: {errorMessage}.");
            }

            return DownloadClientHelpers.GrabFailure(client, request, "Deluge did not add the release: it may already hold it.");
        }

        await TryLabelAsync(http, baseUri, added.Result, DownloadClientHelpers.ResolveCategory(client, request), cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.SaveFolder))
        {
            await TryPinFolderAsync(http, baseUri, added.Result, request.SaveFolder, cancellationToken);
        }

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
        // ratio/seeding_time: #661 — without them a sharing rule could never be
        // seen as met for Deluge. tracker_status: the field that actually
        // carries a ban/unregistered/offline tracker error; message is blanked
        // whenever it reads "OK" and says nothing else useful.
        var payload = new DelugeRequest("web.update_ui", [new[] { "name", "state", "progress", "download_payload_rate", "upload_payload_rate", "eta", "total_size", "total_done", "num_peers", "num_seeds", "time_added", "label", "message", "save_path", "move_completed", "move_completed_path", "ratio", "seeding_time", "tracker_status", "trackers", "private", "queue", "stop_at_ratio", "stop_ratio", "remove_at_ratio" }, new Dictionary<string, object>()], 2);
        var response = await DownloadClientHelpers.PostJsonAsync<DelugeResponse<DelugeUpdateResult>>(http, new Uri(baseUri, "json"), payload, cancellationToken);
        var torrents = response?.Result?.Torrents ?? new Dictionary<string, DelugeTorrent>();
        // Where each label finally puts a finished download (#1150), asked only
        // when a labelled torrent has finished: the Label plugin moves it after
        // it reports finished, and nothing on the torrent says so beforehand.
        var finalFolders = await FinalFoldersAsync(http, new Uri(baseUri, "json"), torrents.Values, cancellationToken);
        var queue = torrents.Select(pair =>
        {
            var item = pair.Value;
            return new DownloadQueueItem(pair.Key, client.Id, client.Name, client.Protocol, DownloadClientHelpers.InferMediaType(client, item.Label),
                DownloadClientHelpers.CleanReleaseTitle(item.Name ?? "Unknown Deluge item"), item.Name ?? "Unknown Deluge item", item.Label ?? string.Empty,
                StillMoving(item, finalFolders) ? DownloadQueueStatuses.Downloading : NormalizeStatus(item.State, item.Progress), Math.Clamp((item.Progress ?? 0), 0, 100), ((item.DownloadPayloadRate ?? 0) / 1_000_000d),
                Math.Max(0, Convert.ToInt32(item.Eta ?? 0)), Convert.ToInt64(item.TotalSize ?? 0), Convert.ToInt64(item.TotalDone ?? 0), (item.NumPeers ?? 0) + (item.NumSeeds ?? 0), "Deluge",
                (IsComplete(item) ? null : TrackerError(item.TrackerStatus)) ?? DelugeMessage(item.Message), DownloadClientHelpers.FromUnix(Convert.ToInt64(item.TimeAdded ?? 0)), DownloadClientHelpers.ResolveDownloadPath(item.SavePath, item.Name),
                // Both come back on the same web.update_ui call, so a sharing
                // rule costs no extra request. seeding_time is seconds since
                // the torrent completed; it is 0 while still downloading.
                Ratio: item.Ratio,
                SeedingMinutes: item.SeedingTime is null ? null : (int)Math.Clamp(Convert.ToInt64(item.SeedingTime.Value) / 60, 0, int.MaxValue),
                UploadSpeedMbps: ((item.UploadPayloadRate ?? 0) / 1_000_000d),
                // #880/#881: tracker_status carries a tracker banning,
                // unregistering, or dropping the release — a release-side
                // finding. Deluge's own message field is the daemon speaking
                // for itself (a move that failed, a path it could not write
                // to), which is this machine's problem.
                IsSetupProblem: (IsComplete(item) || TrackerError(item.TrackerStatus) is null) && DelugeMessage(item.Message) is not null,
                IsPrivate: item.Private,
                // #878: tracker_status is Deluge's own free-text field, not a
                // fixed enum, so a keyword match on the raw text (not only the
                // "Error"-prefixed slice ErrorMessage above uses) is what
                // catches the phrasing a tracker actually sent back.
                TrackerStatus: DownloadClientHelpers.LooksUnregistered(item.TrackerStatus)
                    ? DownloadTrackerStatuses.Unregistered
                    : string.IsNullOrWhiteSpace(item.TrackerStatus) ? DownloadTrackerStatuses.Unknown : DownloadTrackerStatuses.Ok,
                // #1026: Deluge's own queue field counts from 0 and is -1 for a
                // torrent that is not queued. stop_at_ratio/stop_ratio are the
                // torrent's own seed limit (copied from the global one when added).
                QueuePosition: item.Queue is >= 0 ? item.Queue + 1 : null,
                SeedLimit: SeedLimitText.ForDeluge(item.StopAtRatio, item.StopRatio, item.RemoveAtRatio),
                // #1194: Deluge's label is its only tag, and its trackers list holds the announce URLs.
                Tags: string.IsNullOrWhiteSpace(item.Label) ? [] : [item.Label],
                TrackerHosts: DownloadClientHelpers.HostsOf((item.Trackers ?? []).Select(tracker => tracker.Url)));
        })
            .Select(item => DownloadClientHelpers.ReconcileTorrentStatus(item, capturedUtc))
            .ToArray();
        var freeSpaceBytes = await GetFreeSpaceBytesAsync(http, baseUri, cancellationToken);
        var clientVersion = await GetVersionAsync(http, baseUri, cancellationToken);
        var (health, message) = DownloadClientHelpers.EvaluateDiskSpaceHealth(
            $"Connected to Deluge at {baseUri.Host}:{baseUri.Port}.",
            freeSpaceBytes,
            null);
        return CreateSnapshot(client, queue, capturedUtc, health, message, freeSpaceBytes: freeSpaceBytes, clientVersion: clientVersion);
    }

    /// <summary>
    /// Deluge's own version (#1117): the daemon's <c>daemon.info</c> call, which answers the bare version
    /// string ("2.1.1"); an older daemon that only has <c>daemon.get_version</c> is asked that instead. Null
    /// when neither answers.
    /// </summary>
    private static async Task<string?> GetVersionAsync(HttpClient http, Uri baseUri, CancellationToken cancellationToken)
    {
        foreach (var method in new[] { "daemon.info", "daemon.get_version" })
        {
            try
            {
                using var response = await http.PostSizedJsonAsync(new Uri(baseUri, "json"), new DelugeRequest(method, [], 9), cancellationToken);
                if (response.IsSuccessStatusCode
                    && Deluno.Connections.Versions.ConnectionVersionParsers.FromDeluge(await response.Content.ReadAsStringAsync(cancellationToken)) is { } version)
                {
                    return version;
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                // Try the other name, then say nothing.
            }
        }

        return null;
    }

    /// <summary>
    /// Deluge's <c>core.get_free_space</c>, asked with no path so it answers
    /// about its own default download location (#909).
    /// </summary>
    private static async Task<long?> GetFreeSpaceBytesAsync(HttpClient http, Uri baseUri, CancellationToken cancellationToken)
    {
        try
        {
            var response = await DownloadClientHelpers.PostJsonAsync<DelugeResponse<long?>>(
                http, new Uri(baseUri, "json"), new DelugeRequest("core.get_free_space", [], 8), cancellationToken);
            return response?.Result;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Deluge's <c>core.get_torrent_status</c> asked only for <c>files</c>
    /// (#816): each entry's own path relative to the torrent's folder. Null
    /// when the address is missing or the call fails.
    /// </summary>
    public override async Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null || string.IsNullOrWhiteSpace(queueItemId)) return null;

        try
        {
            var http = CreateHttp();
            await LoginAsync(http, baseUri, client, cancellationToken);
            var payload = new DelugeRequest("core.get_torrent_status", [queueItemId, new[] { "files" }], 2);
            var response = await DownloadClientHelpers.PostJsonAsync<DelugeResponse<DelugeFilesResult>>(http, new Uri(baseUri, "json"), payload, cancellationToken);
            return response?.Result?.Files
                ?.Select(file => file.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
                .ToArray();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    public override async Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
    {
        // One arm per verb. `remove_data` is the second argument to
        // core.remove_torrent and is the only thing separating two of these, so
        // it is set by the arm rather than recomputed from the action name.
        //
        // `remove-refused-copy` is deliberately the same request as `delete-with-data` here,
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
            DownloadClientActions.RemoveRefusedCopy => ("core.remove_torrent", true),
            // #810: Deluge's own core.queue_* methods.
            DownloadClientActions.MoveToTop => ("core.queue_top", null),
            DownloadClientActions.MoveUp => ("core.queue_up", null),
            DownloadClientActions.MoveDown => ("core.queue_down", null),
            DownloadClientActions.MoveToBottom => ("core.queue_bottom", null),
            _ => (null, null)
        };
        if (method is null) return DownloadClientHelpers.Unsupported(client, queueItemId, action, "Deluge");
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.MissingAddress(client, queueItemId, action);
        var http = CreateHttp();
        await LoginAsync(http, baseUri, client, cancellationToken);
        // core.remove_torrent takes one torrent id, not a list: a list is
        // refused with an error Deluno used to ignore, so a "removed" download
        // stayed in Deluge (#1148). The others take the list form.
        object[] parameters = removeData is { } wipe
            ? [queueItemId, wipe]
            : [new[] { queueItemId }];
        var response = await DownloadClientHelpers.PostJsonAsync<DelugeResponse<object>>(http, new Uri(baseUri, "json"), new DelugeRequest(method, parameters, 3), cancellationToken);
        // Deluge answers 200 with the refusal inside the body. Saying "sent"
        // without reading it is how a removal that never happened was reported
        // as done.
        if (response?.Error is { } rejected)
        {
            return DownloadClientHelpers.ActionFailure(
                client,
                queueItemId,
                action,
                string.IsNullOrWhiteSpace(rejected.Message) ? "Deluge refused the request." : $"Deluge refused the request: {rejected.Message}");
        }

        if (removeData is not null && response?.Result is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.False })
        {
            return DownloadClientHelpers.ActionFailure(client, queueItemId, action, "Deluge did not remove the torrent.");
        }

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

    /// <summary>
    /// Deluge's <c>tracker_status</c> carries the one fact <c>message</c> does
    /// not: a tracker that has banned or unregistered this torrent, or gone
    /// offline (#661). Healthy values read "Announce sent" / "Announce OK" and
    /// carry nothing worth showing; only the ones Deluge itself prefixes
    /// "Error" are a problem, in Deluge's own words.
    /// </summary>
    private static string? TrackerError(string? trackerStatus)
        => !string.IsNullOrWhiteSpace(trackerStatus) && trackerStatus.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
            ? DownloadClientHelpers.PlainConnectionFailure(trackerStatus, "The tracker") ?? trackerStatus.Trim()
            : null;

    /// <summary>
    /// #1183: a torrent at 100% in any state but Error is finished. Its tracker
    /// matters only while it still downloads, so a tracker that does not
    /// answer is never an error on it: it would otherwise make a good, seeding
    /// download read as failed and never be handed to the processor.
    /// </summary>
    private static bool IsComplete(DelugeTorrent item)
        => Deluno.Platform.Contracts.DownloadHealthStrikes.IsFinishedPercent(item.Progress) && !string.Equals(item.State, "Error", StringComparison.OrdinalIgnoreCase);

    private static bool StillMoving(DelugeTorrent item, IReadOnlyDictionary<string, string> finalFolders)
        => StillMoving(item)
           // The Label plugin's own move (#1150): finished, labelled, and still
           // saved somewhere other than the folder its label moves to.
           || item.Progress >= 100
              && !string.IsNullOrWhiteSpace(item.Label)
              && !string.Equals(item.State, "Error", StringComparison.OrdinalIgnoreCase)
              && finalFolders.TryGetValue(item.Label.Trim().ToLowerInvariant(), out var finalFolder)
              && !SameFolder(item.SavePath, finalFolder);

    private static bool SameFolder(string? left, string? right)
        => string.Equals((left ?? string.Empty).TrimEnd('\\', '/'), (right ?? string.Empty).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// For each label with a finished torrent that has no move of its own, the
    /// folder Deluge will move it to (#1150). A label that moves nothing is
    /// left out, as is anything Deluge would not tell us: unknown reads as
    /// "already where it is going", the behaviour before this check.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> FinalFoldersAsync(
        HttpClient http,
        Uri json,
        IEnumerable<DelugeTorrent> torrents,
        CancellationToken cancellationToken)
    {
        var labels = torrents
            .Where(torrent => torrent.Progress >= 100
                && !string.IsNullOrWhiteSpace(torrent.Label)
                && !string.Equals(torrent.State, "Moving", StringComparison.OrdinalIgnoreCase)
                && !StillMoving(torrent))
            .Select(torrent => torrent.Label!.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (labels.Length == 0)
        {
            return folders;
        }

        try
        {
            var core = await DownloadClientHelpers.PostJsonAsync<DelugeResponse<JsonElement>>(
                http, json, new DelugeRequest("core.get_config_values", [new[] { "move_completed", "move_completed_path" }], 20), cancellationToken);
            var coreConfig = core?.Result ?? default;
            var coreMoves = coreConfig.ValueKind == JsonValueKind.Object && coreConfig.TryGetProperty("move_completed", out var moves) && moves.ValueKind == JsonValueKind.True;
            var corePath = coreMoves && coreConfig.TryGetProperty("move_completed_path", out var coreValue) && coreValue.ValueKind == JsonValueKind.String ? coreValue.GetString() : null;

            foreach (var label in labels)
            {
                var options = await DownloadClientHelpers.PostJsonAsync<DelugeResponse<JsonElement>>(http, json, new DelugeRequest("label.get_options", [label], 21), cancellationToken);
                var own = options?.Result ?? default;
                bool Flag(string key) => own.ValueKind == JsonValueKind.Object && own.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
                var ownPath = own.ValueKind == JsonValueKind.Object && own.TryGetProperty("move_completed_path", out var path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null;
                var folder = Flag("apply_move_completed") && Flag("move_completed") && !string.IsNullOrWhiteSpace(ownPath) ? ownPath : corePath;
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    folders[label] = folder;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidOperationException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return folders;
    }

    private static bool StillMoving(DelugeTorrent item)
        => string.Equals(item.State, "Moving", StringComparison.OrdinalIgnoreCase)
           || item.Progress >= 100 && item.MoveCompleted == true && !string.IsNullOrWhiteSpace(item.MoveCompletedPath)
              // Deluge's own paths, whichever system it runs on: trimmed of either
              // separator, not by this host's rules (CI runs Linux, Deluge Windows).
              && !string.Equals((item.SavePath ?? string.Empty).TrimEnd('\\', '/'), item.MoveCompletedPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether Deluge has the label and which folder a finished download of it
    /// ends up in (#616). A label only has its own folder when it is set to
    /// move finished downloads; otherwise Deluge's own completed folder applies,
    /// or its download folder when Deluge moves nothing.
    /// </summary>
    public override async Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken)
    {
        var label = category.Trim().ToLowerInvariant();
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (label.Length == 0 || baseUri is null)
        {
            return new(client.Id, client.Name, label, DownloadClientCategoryStatuses.Configuration,
                label.Length == 0 ? "Enter a category before checking it." : "Add the Deluge address before checking a category.", Supported: true, Found: false);
        }

        try
        {
            var http = CreateHttp();
            await LoginAsync(http, baseUri, client, cancellationToken);
            var json = new Uri(baseUri, "json");
            var core = await DownloadClientHelpers.PostJsonAsync<DelugeResponse<JsonElement>>(
                http, json, new DelugeRequest("core.get_config_values", [new[] { "download_location", "move_completed", "move_completed_path" }], 10), cancellationToken);
            var coreConfig = core?.Result ?? default;
            string? CoreText(string key) => coreConfig.ValueKind == JsonValueKind.Object && coreConfig.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var coreMoves = coreConfig.ValueKind == JsonValueKind.Object && coreConfig.TryGetProperty("move_completed", out var moves) && moves.ValueKind == JsonValueKind.True;
            var coreFolder = coreMoves ? CoreText("move_completed_path") : CoreText("download_location");
            // Where a torrent is written while it downloads (#1164); with move-completed off that is also where it finishes.
            var coreIncomplete = CoreText("download_location");

            var labels = await DownloadClientHelpers.PostJsonAsync<DelugeResponse<string[]>>(http, json, new DelugeRequest("label.get_labels", [], 11), cancellationToken);
            if (labels?.Error is not null || labels?.Result is null)
            {
                return new(client.Id, client.Name, label, DownloadClientCategoryStatuses.Unsupported,
                    $"Deluge's Label plugin is off, so every download goes to {coreFolder} whatever its category. Turn on the Label plugin in Deluge, Preferences, Plugins.",
                    Supported: false, Found: false, SavePath: coreFolder, IncompletePath: coreIncomplete);
            }

            if (!labels.Result.Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                return new(client.Id, client.Name, label, DownloadClientCategoryStatuses.Missing,
                    $"Deluge has no label named {label}. Deluno adds it on the first grab, but a new label has no folder, so its downloads go to {coreFolder}. Add the label in Deluge and set its completed folder.",
                    Supported: true, Found: false, SavePath: coreFolder, IncompletePath: coreIncomplete);
            }

            var options = await DownloadClientHelpers.PostJsonAsync<DelugeResponse<JsonElement>>(http, json, new DelugeRequest("label.get_options", [label], 12), cancellationToken);
            var own = options?.Result ?? default;
            bool Flag(string key) => own.ValueKind == JsonValueKind.Object && own.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
            var ownPath = own.ValueKind == JsonValueKind.Object && own.TryGetProperty("move_completed_path", out var path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null;
            var labelMoves = Flag("apply_move_completed") && Flag("move_completed") && !string.IsNullOrWhiteSpace(ownPath);
            var folder = labelMoves ? ownPath : coreFolder;
            return labelMoves
                ? new(client.Id, client.Name, label, DownloadClientCategoryStatuses.Ready,
                    $"Deluge's {label} label moves finished downloads to {folder}.", Supported: true, Found: true, SavePath: folder, IncompletePath: coreIncomplete)
                : new(client.Id, client.Name, label, DownloadClientCategoryStatuses.Configuration,
                    $"Deluge has a {label} label, but it has no completed folder of its own, so its downloads end up in {folder} with everything else. Set \"Move completed to\" on the label in Deluge.",
                    Supported: true, Found: true, SavePath: folder, IncompletePath: coreIncomplete);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidOperationException)
        {
            return new(client.Id, client.Name, label, DownloadClientCategoryStatuses.Unreachable,
                $"Deluno could not read Deluge's labels: {ex.Message}", Supported: true, Found: false);
        }
    }

    public override string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
        // Deluge's own "Paused" state is a person stopping the torrent by
        // hand, distinct from being held back behind others in the queue
        // (#661). Checked ahead of the shared text-status helper, which
        // collapses any "pause" text into the same Queued every other client
        // using it (uTorrent, NZBGet) still means.
        => string.Equals(nativeStatus, "Paused", StringComparison.OrdinalIgnoreCase)
            ? DownloadQueueStatuses.Paused
            : DownloadClientHelpers.NormalizeTextStatus(nativeStatus, progress);

    private HttpClient CreateHttp()
    {
        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(20);
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

    /// <summary>
    /// Deluge's Label plugin copies the label's own "move completed to" onto a
    /// torrent when the label is set, over what the add said. Said again after
    /// the label, the library's folder is the one that stays (#1073).
    /// </summary>
    private static async Task TryPinFolderAsync(HttpClient http, Uri baseUri, string torrentId, string folder, CancellationToken cancellationToken)
    {
        try
        {
            await DownloadClientHelpers.PostJsonAsync<DelugeResponse<object>>(
                http,
                new Uri(baseUri, "json"),
                new DelugeRequest("core.set_torrent_options", [new[] { torrentId }, new Dictionary<string, object> { ["move_completed"] = true, ["move_completed_path"] = folder }], 13),
                cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or NotSupportedException)
        {
            // The add already carried the folder; only the label override is unguarded.
        }
    }

    private static string SafeFileName(string name)
        => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private sealed record DelugeRequest([property: JsonPropertyName("method")] string Method, [property: JsonPropertyName("params")] object[] Params, [property: JsonPropertyName("id")] int Id);
    private sealed record DelugeResponse<T>([property: JsonPropertyName("result")] T? Result, [property: JsonPropertyName("error")] DelugeError? Error = null);
    private sealed record DelugeError([property: JsonPropertyName("message")] string? Message);
    private sealed record DelugeUpdateResult([property: JsonPropertyName("torrents")] IReadOnlyDictionary<string, DelugeTorrent>? Torrents);
    private sealed record DelugeFilesResult([property: JsonPropertyName("files")] IReadOnlyList<DelugeFileEntry>? Files);
    private sealed record DelugeFileEntry([property: JsonPropertyName("path")] string? Path);
    private sealed record DelugeTorrent([property: JsonPropertyName("name")] string? Name, [property: JsonPropertyName("state")] string? State, [property: JsonPropertyName("progress")] double? Progress, [property: JsonPropertyName("download_payload_rate")] double? DownloadPayloadRate, [property: JsonPropertyName("upload_payload_rate")] double? UploadPayloadRate, [property: JsonPropertyName("eta")] double? Eta, [property: JsonPropertyName("total_size")] double? TotalSize, [property: JsonPropertyName("total_done")] double? TotalDone, [property: JsonPropertyName("num_peers")] int? NumPeers, [property: JsonPropertyName("time_added")] double? TimeAdded, [property: JsonPropertyName("label")] string? Label, [property: JsonPropertyName("message")] string? Message, [property: JsonPropertyName("save_path")] string? SavePath, [property: JsonPropertyName("move_completed")] bool? MoveCompleted = null, [property: JsonPropertyName("move_completed_path")] string? MoveCompletedPath = null, [property: JsonPropertyName("ratio")] double? Ratio = null, [property: JsonPropertyName("seeding_time")] double? SeedingTime = null, [property: JsonPropertyName("tracker_status")] string? TrackerStatus = null, [property: JsonPropertyName("private")] bool? Private = null, [property: JsonPropertyName("queue")] int? Queue = null, [property: JsonPropertyName("stop_at_ratio")] bool? StopAtRatio = null, [property: JsonPropertyName("stop_ratio")] double? StopRatio = null, [property: JsonPropertyName("remove_at_ratio")] bool? RemoveAtRatio = null, [property: JsonPropertyName("num_seeds")] int? NumSeeds = null, [property: JsonPropertyName("trackers")] IReadOnlyList<DelugeTracker>? Trackers = null);

    private sealed record DelugeTracker([property: JsonPropertyName("url")] string? Url);
}
