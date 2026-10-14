using System.Globalization;
using Deluno.Infrastructure.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Connections.Contracts;
using Deluno.Contracts;

namespace Deluno.Integrations.DownloadClients.Clients;

public sealed class TransmissionDownloadClient(IHttpClientFactory httpClientFactory) : DownloadClientBase
{
    public override string Protocol => "transmission";
    public override DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, false, true, true, true, true, "basic", SupportsQueueReorder: true);

    public override bool SetsSaveFolderPerGrab => true;

    public override async Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await GrabCoreAsync(client, request, cancellationToken);
        }
        catch (Exception exception) when (ClientCredentials.MessageFor(client, exception) is { } message)
        {
            // #1312: a refused key or login is the snapshot's to say (once, in the client's health); this call keeps its contract.
            return ClientCredentials.GrabFailure(client, request, message);
        }
    }

    private async Task<DownloadClientGrabResult> GrabCoreAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
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
            // its own. #1073: the library's own downloads folder when Deluno has
            // one to name; otherwise Transmission's download folder plus the
            // category, the way Radarr and Sonarr do it. #1164: download-dir is
            // where the torrent FINISHES. With Transmission's incomplete folder
            // on, the in-progress files stay there until it completes; Deluno
            // never touches that setting.
            if (!string.IsNullOrWhiteSpace(request.SaveFolder))
            {
                arguments["download-dir"] = request.SaveFolder;
            }
            else
            {
                var session = await SendAsync(client, baseUri, new TransmissionRequest("session-get", new() { ["fields"] = new[] { "download-dir" } }), cancellationToken);
                if (CategoryFolder(session.Arguments?.DownloadDir, label) is { } folder)
                {
                    arguments["download-dir"] = folder;
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.SaveFolder))
        {
            arguments["download-dir"] = request.SaveFolder;
        }

        var response = await SendAsync(client, baseUri, new TransmissionRequest("torrent-add", arguments), cancellationToken);
        // The legacy protocol answers "success" or a reason in words; only a
        // reason is a refusal.
        //
        // #874/#942, cleanup sheet B2: that reason is Transmission looking at
        // the exact link or file it was handed and refusing it outright, in
        // the same request — "invalid or corrupt torrent file" for a file it
        // could not parse, or a fetch failure for a URL that led nowhere —
        // the same shape qBittorrent's "Fails." and NZBGet's RPC error
        // already read as a dead release rather than a setup problem.
        if (response.Result is { ValueKind: System.Text.Json.JsonValueKind.String } result
            && !string.Equals(result.GetString(), "success", StringComparison.OrdinalIgnoreCase))
        {
            return DownloadClientHelpers.DeadReleaseGrabFailure(client, request, $"Transmission refused the release: {result.GetString()}.");
        }

        // Its id, as the queue reports it, so the import can find this grab by
        // it (#617). Transmission's own RPC says outright, in
        // torrent-duplicate, when it already held this exact torrent (#877,
        // #911) — no before/after hash guessing needed, unlike every other
        // torrent client here.
        var duplicate = response.Arguments?.Duplicate;
        var added = response.Arguments?.Added ?? duplicate;
        return DownloadClientHelpers.GrabSuccess(
            client,
            request,
            duplicate is not null
                ? "Transmission already holds this release. Deluno is following the copy it has instead of sending another."
                : request.TorrentFile is null ? "Release URL sent to Transmission." : "Torrent file sent to Transmission.") with
        {
            // Its hash, which Deluno also worked out before sending (#1062); the
            // session's number is only the last resort, for a torrent Deluno
            // could not hash and Transmission did not name.
            ExternalId = added?.HashString?.Trim().ToLowerInvariant() is { Length: > 0 } hashString
                ? hashString
                : DownloadClientHelpers.KnownHashOf(request) ?? added?.Id?.ToString(CultureInfo.InvariantCulture),
            IsDuplicateFollow = duplicate is not null
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
            // activityDate/doneDate: #973 — activityDate is Transmission's own
            // "last saw any activity" clock (an announce, a piece received or
            // sent); doneDate is when the download itself finished and stays
            // 0 until then. Read together so LastActivityUtc below reflects
            // whichever is more recent, the same "most recent evidence of
            // life" a person actually wants from "last activity".
            ["fields"] = new[] { "id", "hashString", "name", "status", "percentDone", "rateDownload", "rateUpload", "eta", "totalSize", "downloadedEver", "peersConnected", "peersSendingToUs", "addedDate", "activityDate", "doneDate", "downloadDir", "labels", "error", "errorString", "isStalled", "uploadRatio", "secondsSeeding", "isPrivate", "trackerStats", "queuePosition", "seedRatioMode", "seedRatioLimit", "seedIdleMode", "seedIdleLimit" }
        }), cancellationToken);
        var queue = (response.Arguments?.Torrents ?? []).Select(item => new DownloadQueueItem(
            StableId(item) ?? item.Name ?? Guid.CreateVersion7().ToString("N"), client.Id, client.Name, client.Protocol,
            DownloadClientHelpers.InferMediaType(client, item.Labels?.FirstOrDefault()), DownloadClientHelpers.CleanReleaseTitle(item.Name ?? "Unknown Transmission item"),
            item.Name ?? "Unknown Transmission item", item.Labels?.FirstOrDefault() ?? string.Empty, Status(item),
            Math.Clamp(((item.PercentDone ?? 0) * 100), 0, 100), ((item.RateDownload ?? 0) / 1_000_000d), Math.Max(0, item.Eta ?? 0),
            item.TotalSize ?? 0, item.DownloadedEver ?? 0, item.PeersConnected ?? 0, "Transmission", IsFinishedWithTrackerError(item) || string.IsNullOrWhiteSpace(item.ErrorString) ? null : item.ErrorString,
            DownloadClientHelpers.FromUnix(item.AddedDate), DownloadClientHelpers.ResolveDownloadPath(item.DownloadDir, item.Name),
            // Both come back on the same torrent-get call, so a sharing rule
            // costs no extra request. secondsSeeding is 0 while still downloading.
            Ratio: item.UploadRatio,
            SeedingMinutes: item.SecondsSeeding is null ? null : (int)Math.Clamp(item.SecondsSeeding.Value / 60, 0, int.MaxValue),
            UploadSpeedMbps: ((item.RateUpload ?? 0) / 1_000_000d),
            // #880/#881: Transmission's own error field distinguishes a local
            // problem from a tracker one. TR_STAT_LOCAL_ERROR (3) is
            // Transmission's word for a problem on this machine — disk space,
            // permissions, a missing file — while TR_STAT_TRACKER_WARNING (1)
            // and TR_STAT_TRACKER_ERROR (2) are the tracker refusing or
            // complaining about the release itself, which stays a release-side
            // finding.
            IsSetupProblem: item.Error == TrLocalError,
            IsPrivate: item.IsPrivate,
            TrackerStatus: TrackerStatus(item),
            // #973: whichever of activityDate/doneDate is more recent is the
            // real "last saw any activity" answer - doneDate stays fixed once
            // a download finishes, while activityDate keeps moving through
            // seeding, so taking the max never loses ground to a stale field.
            LastActivityUtc: DownloadClientHelpers.FromUnixOrNull(Math.Max(item.ActivityDate ?? 0, item.DoneDate ?? 0)),
            // #1026: queuePosition counts from 0 in Transmission; the screen counts from 1.
            // The seed limits are the torrent's own mode and limit, in plain words.
            QueuePosition: item.QueuePosition is >= 0 ? item.QueuePosition + 1 : null,
            SeedLimit: SeedLimitText.ForTransmission(item.SeedRatioMode, item.SeedRatioLimit, item.SeedIdleMode, item.SeedIdleLimit),
            LegacyId: string.IsNullOrWhiteSpace(item.HashString) ? null : item.Id?.ToString(CultureInfo.InvariantCulture),
            // #1194: for the ignore list.
            Tags: item.Labels ?? [],
            TrackerHosts: DownloadClientHelpers.HostsOf((item.TrackerStats ?? []).Select(stat => stat.Announce))))
            .Select(item => DownloadClientHelpers.ReconcileTorrentStatus(item, capturedUtc))
            .ToArray();
        var (freeSpaceBytes, clientVersion, alternateSpeedLimitEnabled) = await ReadSessionAsync(client, baseUri, cancellationToken);
        var (health, message) = DownloadClientHelpers.EvaluateDiskSpaceHealth(
            $"Connected to Transmission at {baseUri.Host}:{baseUri.Port}.",
            freeSpaceBytes,
            null);
        return CreateSnapshot(client, queue, capturedUtc, health, message, freeSpaceBytes: freeSpaceBytes, clientVersion: clientVersion, alternateSpeedLimitEnabled: alternateSpeedLimitEnabled);
    }

    /// <summary>
    /// The id Deluno files a torrent under: its info-hash (#1062). Transmission's
    /// numeric <c>id</c> belongs to the running session and is handed out
    /// again after a restart, so it cannot say who a torrent is; the hash never
    /// changes. Falls back to the number only for a daemon that sends no hash.
    /// </summary>
    private static string? StableId(TransmissionTorrent item)
        => !string.IsNullOrWhiteSpace(item.HashString)
            ? item.HashString.Trim().ToLowerInvariant()
            : item.Id?.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// What goes in a request's <c>ids</c> list: Transmission accepts either a
    /// number or an info-hash string there. An id that is neither is refused
    /// before anything is sent.
    /// </summary>
    internal static object? RpcId(string queueItemId)
    {
        var value = queueItemId?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        return value.Length is 40 or 64 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Transmission's <c>free-space</c> RPC method, asked about its own
    /// download folder (#909). A daemon too old to know the method, or one
    /// with nothing configured to ask about, reports nothing rather than a
    /// guessed number.
    /// </summary>
    private async Task<(long? FreeSpaceBytes, string? Version, bool? AlternateSpeedLimitEnabled)> ReadSessionAsync(DownloadClientItem client, Uri baseUri, CancellationToken cancellationToken)
    {
        string? version = null;
        bool? alternateSpeedLimit = null;
        try
        {
            // The same session-get also carries Transmission's own version (#1117), "4.0.5 (a6fe2de319)".
            var session = await SendAsync(client, baseUri, new TransmissionRequest("session-get", new() { ["fields"] = new[] { "download-dir", "version", "alt-speed-enabled" } }), cancellationToken);
            version = Deluno.Connections.Versions.ConnectionVersionParsers.Clean(session.Arguments?.Version);
            // #1194: Transmission's alternate speed limit ("turtle mode"), so a Too slow rule can skip while it is on.
            alternateSpeedLimit = session.Arguments?.AltSpeedEnabled;
            var downloadDir = session.Arguments?.DownloadDir;
            if (string.IsNullOrWhiteSpace(downloadDir))
            {
                return (null, version, alternateSpeedLimit);
            }

            var freeSpace = await SendAsync(client, baseUri, new TransmissionRequest("free-space", new() { ["path"] = downloadDir }), cancellationToken);
            // #1060: a download folder that does not exist yet answers nothing; measure the drive it will be created on.
            return (freeSpace.Arguments?.SizeBytes
                ?? (ClientSaveFolders.IsSameMachine(client) ? DownloadClientHelpers.FreeSpaceOfNearestExistingFolder(downloadDir) : null), version, alternateSpeedLimit);
        }
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            return (null, version, alternateSpeedLimit);
        }
    }

    /// <summary>
    /// Transmission's status mapping, with its tracker-error handling. Its own
    /// <c>isStalled</c> flag is deliberately not read as Stalled (#1193, which
    /// replaces #661's immediate promotion): a torrent may only be closing its
    /// last blocks, so it stays Downloading until Deluno's own no-progress
    /// window finds no byte gain, the same rule for every client.
    /// </summary>
    /// <summary>
    /// #1183: a torrent at 100% whose only complaint is its tracker (error 1 =
    /// warning, 2 = tracker error) is finished. The tracker matters only while
    /// it still downloads, so this never makes it stalled or failed. A local
    /// error (3) still counts.
    /// </summary>
    private static bool IsFinishedWithTrackerError(TransmissionTorrent item)
        => item.Error is 1 or 2 && (item.PercentDone ?? 0) >= 1;

    private string Status(TransmissionTorrent item)
    {
        var finished = IsFinishedWithTrackerError(item);
        var status = NormalizeStatus(item.Status?.ToString(CultureInfo.InvariantCulture), item.PercentDone, finished ? null : item.Error, finished ? null : item.ErrorString);
        // isStalled is the client's own flag, not proof (#1193): the status stays as mapped (Downloading) until
        // Deluno's own no-progress window (DownloadClientHelpers.ReconcileTorrentStatus) finds no byte gain.
        return status;
    }

    /// <summary>
    /// #878: Transmission's <c>trackerStats</c> carries the tracker's own
    /// last announce result, unlike the queue-level <c>error</c>/<c>errorString</c>
    /// fields this adapter already reads, which only ever hold Transmission's
    /// own word for the problem. Only an announce Transmission actually made
    /// and that the tracker rejected counts - a tracker it has not reached yet
    /// (no announce attempted) says nothing about whether it still holds the
    /// torrent.
    /// </summary>
    private static string TrackerStatus(TransmissionTorrent item)
    {
        var trackers = item.TrackerStats ?? [];
        if (trackers.Count == 0) return DownloadTrackerStatuses.Unknown;
        return trackers.Any(tracker =>
            tracker.HasAnnounced == true &&
            tracker.LastAnnounceSucceeded == false &&
            DownloadClientHelpers.LooksUnregistered(tracker.LastAnnounceResult))
            ? DownloadTrackerStatuses.Unregistered
            : DownloadTrackerStatuses.Ok;
    }

    /// <summary>
    /// Transmission's own <c>files</c> field on <c>torrent-get</c> (#816):
    /// every file's path relative to the torrent's own folder. Null when the
    /// address is missing, the id does not parse, or the call fails.
    /// </summary>
    public override async Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null || RpcId(queueItemId) is not { } id) return null;

        try
        {
            var response = await SendAsync(client, baseUri, new TransmissionRequest("torrent-get", new()
            {
                ["ids"] = new[] { id },
                ["fields"] = new[] { "files" }
            }), cancellationToken);
            var torrent = response.Arguments?.Torrents?.FirstOrDefault();
            return torrent?.Files
                ?.Select(file => file.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToArray();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    public override async Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteActionCoreAsync(client, action, queueItemId, cancellationToken);
        }
        catch (Exception exception) when (ClientCredentials.MessageFor(client, exception) is not null)
        {
            // #1251: a wrong login is said as that, not as "returned 401".
            return ClientCredentials.Failure(client, queueItemId, action, ClientCredentials.MessageFor(client, exception)!);
        }
    }

    private async Task<DownloadClientActionResult> ExecuteActionCoreAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
    {
        // One arm per verb, with `delete-local-data` set by the arm rather than
        // recomputed, because that flag is the whole of the difference between
        // two of them. Deluno never deletes a shared file itself (#287).
        //
        // `remove-refused-copy` is the same request as `delete-with-data`, said rather than
        // implied: Transmission keeps no record of a torrent it no longer has,
        // so removing it is the whole of forgetting it.
        var (method, deleteLocalData) = action switch
        {
            DownloadClientActions.Pause => ("torrent-stop", (bool?)null),
            DownloadClientActions.Resume => ("torrent-start", null),
            DownloadClientActions.Recheck => ("torrent-verify", null),
            DownloadClientActions.Delete => ("torrent-remove", false),
            DownloadClientActions.DeleteWithData => ("torrent-remove", true),
            DownloadClientActions.RemoveRefusedCopy => ("torrent-remove", true),
            // #810: Transmission's own queue-move-* RPC methods.
            DownloadClientActions.MoveToTop => ("queue-move-top", null),
            DownloadClientActions.MoveUp => ("queue-move-up", null),
            DownloadClientActions.MoveDown => ("queue-move-down", null),
            DownloadClientActions.MoveToBottom => ("queue-move-bottom", null),
            _ => (null, null)
        };
        if (method is null) return DownloadClientHelpers.Unsupported(client, queueItemId, action, "Transmission");
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.MissingAddress(client, queueItemId, action);
        if (RpcId(queueItemId) is not { } torrentId)
        {
            return DownloadClientHelpers.UnreadableId(client, queueItemId, action, "Transmission");
        }

        // #1224: removing with its data leaves empty `<name>__xxxxxx` folders beside the download. Note where it
        // was before the call, as Transmission forgets it after.
        (string? Name, string? Folder) removed = (null, null);

        async Task<DownloadClientActionResult> Send()
        {
            if (deleteLocalData == true && ClientSaveFolders.IsSameMachine(client))
            {
                removed = await ReadNameAndFolderAsync(client, baseUri, torrentId, cancellationToken);
            }

            var arguments = new Dictionary<string, object> { ["ids"] = new[] { torrentId } };
            if (deleteLocalData is { } wipe) arguments["delete-local-data"] = wipe;
            await SendAsync(client, baseUri, new TransmissionRequest(method, arguments), cancellationToken);
            var done = DownloadClientHelpers.ActionSuccess(client, queueItemId, action, "Transmission action sent.");
            var leftover = DownloadFolderRemoval.RemoveEmptyLeftovers(removed.Folder, removed.Name);
            return leftover is null ? done : done with { FilesNote = leftover };
        }

        // #1251: a pause or a resume is done only when Transmission had the torrent before and shows it in the asked state after.
        if (action is DownloadClientActions.Pause or DownloadClientActions.Resume)
        {
            return await ClientPauseGuard.RunAsync(client, queueItemId, action, "Transmission",
                async () =>
                {
                    try
                    {
                        var response = await SendAsync(client, baseUri, new TransmissionRequest("torrent-get", new()
                        {
                            ["ids"] = new[] { torrentId },
                            ["fields"] = new[] { "id", "hashString", "status" }
                        }), cancellationToken);
                        if (response.Arguments?.Torrents is not { } torrents)
                        {
                            return new ClientPauseReading(ClientHas.Unknown, false);
                        }

                        var torrent = torrents.Count == 1
                            ? torrents[0]
                            : torrents.FirstOrDefault(item => string.Equals(item.HashString, queueItemId, StringComparison.OrdinalIgnoreCase)
                                || (torrentId is int number && item.Id == number));
                        // Status 0 is Transmission's "stopped".
                        return torrent is null
                            ? new ClientPauseReading(ClientHas.No, false)
                            : new ClientPauseReading(ClientHas.Yes, torrent.Status == 0);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !ClientCredentials.IsRefusal(exception))
                    {
                        return new ClientPauseReading(ClientHas.Unknown, false);
                    }
                },
                Send, ClientPauseGuard.SettleWindow, ClientPauseGuard.SettlePoll, cancellationToken);
        }

        // #1252: a removal is done only when Transmission had the torrent before and no longer has it after (#1212).
        if (!DownloadClientActions.RemovesTheDownload(action))
        {
            return await Send();
        }

        return await ClientRemovalGuard.RunAsync(client, queueItemId, action, "Transmission",
            async () =>
            {
                try
                {
                    var response = await SendAsync(client, baseUri, new TransmissionRequest("torrent-get", new()
                    {
                        ["ids"] = new[] { torrentId },
                        ["fields"] = new[] { "id" }
                    }), cancellationToken);
                    return response.Arguments?.Torrents is { } torrents
                        ? torrents.Count > 0 ? ClientHas.Yes : ClientHas.No
                        : ClientHas.Unknown;
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException && !ClientCredentials.IsRefusal(exception))
                {
                    return ClientHas.Unknown;
                }
            },
            Send, ClientRemovalGuard.TorrentSettleWindow, ClientRemovalGuard.TorrentSettlePoll, cancellationToken);
    }

    /// <summary>
    /// #1240: puts a torrent under a keep-seeding label with <c>torrent-set</c>. The torrent's label set is rewritten:
    /// the keep-seeding label goes first, the label Deluno knew it by is dropped, and every other label stays. Its
    /// download folder is not touched. Labels need RPC 16 (Transmission 2.40); an older daemon says so.
    /// </summary>
    public override async Task<DownloadClientActionResult> SetCategoryAsync(
        DownloadClientItem client, string queueItemId, string category, string? currentCategory, CancellationToken cancellationToken)
    {
        try
        {
            return await SetCategoryCoreAsync(client, queueItemId, category, currentCategory, cancellationToken);
        }
        catch (Exception exception) when (ClientCredentials.MessageFor(client, exception) is { } message)
        {
            // #1312: a refused key or login is the snapshot's to say (once, in the client's health); this call keeps its contract.
            return ClientCredentials.Failure(client, queueItemId, DownloadClientActions.SetCategory, message);
        }
    }

    private async Task<DownloadClientActionResult> SetCategoryCoreAsync(
        DownloadClientItem client, string queueItemId, string category, string? currentCategory, CancellationToken cancellationToken)
    {
        var action = DownloadClientActions.SetCategory;
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.MissingAddress(client, queueItemId, action);
        if (RpcId(queueItemId) is not { } torrentId)
        {
            return DownloadClientHelpers.UnreadableId(client, queueItemId, action, "Transmission");
        }

        var session = await SendAsync(client, baseUri, new TransmissionRequest("session-get", new() { ["fields"] = new[] { "rpc-version" } }), cancellationToken);
        if (session.Arguments?.RpcVersion is null or < MinimumLabelRpcVersion)
        {
            return DownloadClientHelpers.ActionFailure(client, queueItemId, action,
                "This Transmission is too old for labels (they need Transmission 2.40 or later), so it cannot keep a download seeding under one.");
        }

        var label = category.Trim();
        return await ClientCategoryMove.RunAsync(
            client, queueItemId, label, "Transmission",
            async () =>
            {
                var response = await SendAsync(client, baseUri, new TransmissionRequest("torrent-get", new()
                {
                    ["ids"] = new[] { torrentId },
                    ["fields"] = new[] { "labels" }
                }), cancellationToken);
                var torrent = response.Arguments?.Torrents?.FirstOrDefault();
                return torrent is null
                    ? new ClientCategoryReading(ClientHas.No, null)
                    : new ClientCategoryReading(ClientHas.Yes, torrent.Labels?.FirstOrDefault());
            },
            async _ =>
            {
                // The labels as they are now, so the others survive the rewrite.
                var current = await SendAsync(client, baseUri, new TransmissionRequest("torrent-get", new()
                {
                    ["ids"] = new[] { torrentId },
                    ["fields"] = new[] { "labels" }
                }), cancellationToken);
                var existing = current.Arguments?.Torrents?.FirstOrDefault()?.Labels ?? [];
                var labels = new List<string> { label };
                labels.AddRange(existing.Where(item =>
                    !string.IsNullOrWhiteSpace(item)
                    && !ClientCategoryMove.SameCategory(item, label)
                    && !ClientCategoryMove.SameCategory(item, currentCategory)));
                var set = await SendAsync(client, baseUri, new TransmissionRequest("torrent-set", new()
                {
                    ["ids"] = new[] { torrentId },
                    ["labels"] = labels.ToArray()
                }), cancellationToken);
                var result = set.Result is { ValueKind: System.Text.Json.JsonValueKind.String } text ? text.GetString() : null;
                return string.Equals(result, "success", StringComparison.OrdinalIgnoreCase) || result is null
                    ? DownloadClientHelpers.ActionSuccess(client, queueItemId, action, "Transmission action sent.")
                    : DownloadClientHelpers.ActionFailure(client, queueItemId, action, $"Transmission refused to set the label: {result}.");
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
                name.Length == 0 ? "Enter a category before checking it." : "Add the Transmission address before checking a category.", Supported: true, Found: false);
        }

        try
        {
            var session = await SendAsync(client, baseUri, new TransmissionRequest("session-get", new() { ["fields"] = new[] { "rpc-version" } }), cancellationToken);
            var rpcVersion = session.Arguments?.RpcVersion;
            if (rpcVersion is null or < MinimumLabelRpcVersion)
            {
                return new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Unsupported,
                    "This Transmission is too old for labels (they need Transmission 2.40 or later), so it cannot keep a download seeding under one.", Supported: false, Found: false);
            }

            return new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Ready,
                $"Transmission makes the {name} label when Deluno first uses it, so there is nothing to create.", Supported: true, Found: false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Unreachable,
                $"Deluno could not reach Transmission: {exception.Message}", Supported: true, Found: false);
        }
    }

    private async Task<(string? Name, string? Folder)> ReadNameAndFolderAsync(DownloadClientItem client, Uri baseUri, object torrentId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendAsync(client, baseUri, new TransmissionRequest("torrent-get", new()
            {
                ["ids"] = new[] { torrentId },
                ["fields"] = new[] { "name", "downloadDir" }
            }), cancellationToken);
            var torrent = response.Arguments?.Torrents?.FirstOrDefault();
            return (torrent?.Name, torrent?.DownloadDir);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return (null, null);
        }
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
            var session = await SendAsync(client, baseUri, new TransmissionRequest("session-get", new() { ["fields"] = new[] { "download-dir", "rpc-version", "incomplete-dir", "incomplete-dir-enabled" } }), cancellationToken);

            // #973: labels arrived in Transmission's RPC 16 (Transmission
            // 2.40). Deluno routes every grab by category through a label
            // (#606), so a daemon too old to have one silently drops every
            // download into the same folder - the same shape as Deluge's own
            // Label-plugin-off message, so this reads the same way.
            var rpcVersion = session.Arguments?.RpcVersion;
            if (rpcVersion is null or < MinimumLabelRpcVersion)
            {
                return new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Unsupported,
                    rpcVersion is null
                        ? "Deluno could not read Transmission's RPC version, so it cannot tell whether labels are supported. Labels need Transmission 2.40 (RPC 16) or later."
                        : $"This Transmission speaks RPC {rpcVersion}, older than the RPC {MinimumLabelRpcVersion} (Transmission 2.40) that added labels, so every download goes to the same folder whatever its category. Upgrade Transmission to route by category.",
                    Supported: false, Found: false);
            }

            var folder = CategoryFolder(session.Arguments?.DownloadDir, name);
            return folder is null
                ? new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Configuration,
                    "Transmission did not say where it saves downloads.", Supported: true, Found: false)
                : new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Ready,
                    $"Transmission has no categories of its own; Deluno sends each {name} download to {folder}, inside Transmission's download folder.",
                    Supported: true, Found: true, SavePath: folder,
                    // Transmission writes a download here while it is in progress (#1164); with its incomplete folder off that is the folder itself.
                    IncompletePath: session.Arguments?.IncompleteDirEnabled == true && !string.IsNullOrWhiteSpace(session.Arguments.IncompleteDir) ? session.Arguments.IncompleteDir : folder);
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

    /// <summary>#973: the RPC version Transmission 2.40 introduced, the first to support labels.</summary>
    internal const int MinimumLabelRpcVersion = 16;

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
        [property: JsonPropertyName("size-bytes")] long? SizeBytes = null,
        [property: JsonPropertyName("rpc-version")] int? RpcVersion = null,
        [property: JsonPropertyName("version")] string? Version = null,
        [property: JsonPropertyName("incomplete-dir")] string? IncompleteDir = null,
        [property: JsonPropertyName("incomplete-dir-enabled")] bool? IncompleteDirEnabled = null,
        [property: JsonPropertyName("alt-speed-enabled")] bool? AltSpeedEnabled = null);
    private sealed record TransmissionAdded([property: JsonPropertyName("id")] int? Id, [property: JsonPropertyName("hashString")] string? HashString);
    private sealed record TransmissionTorrent([property: JsonPropertyName("id")] int? Id, [property: JsonPropertyName("name")] string? Name, [property: JsonPropertyName("status")] int? Status, [property: JsonPropertyName("percentDone")] double? PercentDone, [property: JsonPropertyName("rateDownload")] long? RateDownload, [property: JsonPropertyName("rateUpload")] long? RateUpload, [property: JsonPropertyName("eta")] int? Eta, [property: JsonPropertyName("totalSize")] long? TotalSize, [property: JsonPropertyName("downloadedEver")] long? DownloadedEver, [property: JsonPropertyName("peersConnected")] int? PeersConnected, [property: JsonPropertyName("addedDate")] long? AddedDate, [property: JsonPropertyName("downloadDir")] string? DownloadDir, [property: JsonPropertyName("labels")] IReadOnlyList<string>? Labels, [property: JsonPropertyName("error")] int? Error, [property: JsonPropertyName("errorString")] string? ErrorString, [property: JsonPropertyName("peersSendingToUs")] int? PeersSendingToUs = null, [property: JsonPropertyName("isStalled")] bool? IsStalled = null, [property: JsonPropertyName("uploadRatio")] double? UploadRatio = null, [property: JsonPropertyName("secondsSeeding")] long? SecondsSeeding = null, [property: JsonPropertyName("isPrivate")] bool? IsPrivate = null, [property: JsonPropertyName("trackerStats")] IReadOnlyList<TransmissionTrackerStat>? TrackerStats = null, [property: JsonPropertyName("files")] IReadOnlyList<TransmissionFile>? Files = null, [property: JsonPropertyName("activityDate")] long? ActivityDate = null, [property: JsonPropertyName("doneDate")] long? DoneDate = null, [property: JsonPropertyName("queuePosition")] int? QueuePosition = null, [property: JsonPropertyName("seedRatioMode")] int? SeedRatioMode = null, [property: JsonPropertyName("seedRatioLimit")] double? SeedRatioLimit = null, [property: JsonPropertyName("seedIdleMode")] int? SeedIdleMode = null, [property: JsonPropertyName("seedIdleLimit")] int? SeedIdleLimit = null, [property: JsonPropertyName("hashString")] string? HashString = null);

    /// <summary>One tracker's own last-announce result, from Transmission's <c>trackerStats</c> (#878).</summary>
    private sealed record TransmissionTrackerStat(
        [property: JsonPropertyName("lastAnnounceResult")] string? LastAnnounceResult,
        [property: JsonPropertyName("lastAnnounceSucceeded")] bool? LastAnnounceSucceeded,
        [property: JsonPropertyName("hasAnnounced")] bool? HasAnnounced,
        [property: JsonPropertyName("announce")] string? Announce = null);

    /// <summary>One file inside a torrent (#816); <c>name</c> is its path relative to the torrent's own folder.</summary>
    private sealed record TransmissionFile([property: JsonPropertyName("name")] string? Name);
}
