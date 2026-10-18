using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Deluno.Connections.Contracts;
using Deluno.Contracts;

namespace Deluno.Integrations.DownloadClients.Clients;

/// <summary>
/// rTorrent's own RPC interface (#803): XML-RPC over HTTP, the shape every
/// rTorrent install exposes once it is put behind a webserver (SCGI is
/// rTorrent's native transport, but almost nobody reaches it directly —
/// lighttpd/nginx/xmlrpc2http bridges terminate it as plain HTTP, which is
/// what Deluno's EndpointUrl already models for every other client). SCGI
/// itself is not implemented: nothing in #803's acceptance asked for it, and
/// every rTorrent setup this was checked against fronts it with HTTP anyway.
///
/// <para><b>No native "delete with data".</b> rTorrent's own RPC has
/// <c>d.erase</c> — drop the torrent from rTorrent's session — and nothing
/// that also removes the files it produced. Radarr/Sonarr do not claim to
/// delete rTorrent's payload either; it is a genuine gap in rTorrent's own
/// API, not a shortcut taken here. Delete and RemoveRefusedCopy call <c>d.erase</c> alone:
/// the file leaves rTorrent's session either way, and rTorrent keeps no
/// separate history for a released hash to survive in (unlike SABnzbd/NZBGet),
/// so there is nothing further "remove the refused copy" could do.</para>
///
/// <para><b>DeleteWithData asks rTorrent where the download actually is</b>
/// (<c>d.base_path</c>, falling back to <c>d.directory</c> for an older
/// rTorrent build without it) and deletes exactly that path itself, once
/// <c>d.erase</c> has released rTorrent's own hold on it — never a parent, a
/// sibling, or anything <c>d.base_path</c> did not itself name. When rTorrent
/// cannot say where the download is, or the path it names does not exist on
/// this machine (a remote rTorrent whose files Deluno cannot see), nothing is
/// deleted and the action's own message says so, so the limit shows on screen
/// instead of silently doing nothing.</para>
/// </summary>
public sealed class RtorrentDownloadClient(IHttpClientFactory httpClientFactory, Deluno.Libraries.Data.ILibrariesRepository? libraries = null) : DownloadClientBase
{
    public override string Protocol => "rtorrent";

    public override DownloadClientTelemetryCapabilities Capabilities { get; } = new(
        SupportsQueue: true,
        SupportsHistory: false,
        SupportsPauseResume: true,
        SupportsRemove: true,
        SupportsRecheck: true,
        SupportsImportPath: true,
        AuthMode: "basic");

    public override async Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.GrabFailure(client, request, "Download client address is missing.");
        var http = CreateHttp(client);

        // What was there before, so the answer below can be about what
        // actually happened rather than about what was asked — the same
        // before/after honesty check qBittorrent's adapter uses (#606),
        // needed here for the same reason: load.start answers before the
        // torrent necessarily appears in rTorrent's own list.
        var before = await ListHashesAsync(http, baseUri, cancellationToken);

        // load.normal/load.raw load without starting (for Add downloads
        // paused, #805); load.start/load.raw_start load and start
        // immediately. The "raw" pair take the .torrent's bytes directly
        // (base64, an XML-RPC <base64> value) for a release Deluno fetched
        // itself with a tracker's own sign-in (#606); the plain pair take a
        // URL rTorrent fetches on its own.
        // #874/#942, cleanup sheet B2: rTorrent answers a load call it could
        // not honour — a .torrent it could not parse — with an XML-RPC
        // <fault>, which ParseResponse turns into an
        // RtorrentFaultException. That is rTorrent looking at the exact
        // file it was handed and refusing it outright, in the same request,
        // the same shape qBittorrent's "Fails." and NZBGet's RPC error
        // already read as a dead release rather than a setup problem.
        try
        {
            if (request.TorrentFile is { } file)
            {
                var method = client.AddPaused ? "load.raw" : "load.raw_start";
                await SendAsync(http, baseUri, method, [string.Empty, new XmlRpcBase64(file)], cancellationToken);
            }
            else
            {
                var method = client.AddPaused ? "load.normal" : "load.start";
                await SendAsync(http, baseUri, method, [string.Empty, request.DownloadUrl], cancellationToken);
            }
        }
        catch (RtorrentFaultException fault)
        {
            return DownloadClientHelpers.DeadReleaseGrabFailure(client, request, $"rTorrent refused the release: {fault.FaultMessage}.");
        }

        // #1062: the hash is known before the send (magnet, or the file's own
        // bytes); what rTorrent lists afterwards only confirms it.
        var knownHash = DownloadClientHelpers.KnownHashOf(request);
        var after = await WaitForNewHashAsync(http, baseUri, before, knownHash, cancellationToken);
        if (knownHash is not null && before is not null && before.Contains(knownHash, StringComparer.OrdinalIgnoreCase)
            || knownHash is null && before is not null && after is not null && !after.Except(before).Any())
        {
            // #877: no new hash appearing is also what a link rTorrent could
            // not read looks like, so only a hash Deluno already knows and
            // finds in rTorrent's own list is a genuine duplicate. A magnet
            // link names its own hash, so it can be checked directly against
            // what rTorrent already held before this request — the same
            // check qBittorrent's adapter makes (cleanup sheet B3).
            if (knownHash is { Length: 40 } duplicateHash && before.Contains(duplicateHash, StringComparer.OrdinalIgnoreCase))
            {
                return DownloadClientHelpers.GrabSuccess(
                    client,
                    request,
                    "rTorrent already holds this release. Deluno is following the copy it has instead of sending another.") with
                {
                    ExternalId = duplicateHash,
                    IsDuplicateFollow = true
                };
            }

            return DownloadClientHelpers.GrabFailure(
                client,
                request,
                "rTorrent accepted the request but added no torrent. Either it already holds this release (remove it there, or force a re-download), or it could not read what the link returned.");
        }

        var added = knownHash ?? DownloadClientHelpers.TheOnlyNewItem(before, after);
        return DownloadClientHelpers.GrabSuccess(client, request, request.TorrentFile is null ? "Release URL sent to rTorrent." : "Torrent file sent to rTorrent.") with
        {
            ExternalId = added
        };
    }

    public override async Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return CreateConfigurationSnapshot(client, capturedUtc, "Download client address is missing.");
        var http = CreateHttp(client);

        // d.multicall2 with an explicit field list, in the order MapRow reads
        // them below — one call for the whole "main" view rather than one per
        // torrent, the same batching every other adapter's list call gets for
        // free from its own client's list endpoint.
        var response = await SendAsync(
            http,
            baseUri,
            "d.multicall2",
            [
                string.Empty, "main",
                "d.hash=", "d.name=", "d.is_active=", "d.is_hash_checking=", "d.complete=",
                "d.bytes_done=", "d.size_bytes=", "d.down.rate=", "d.up.rate=", "d.ratio=",
                "d.message=", "d.directory=", "d.peers_connected="
            ],
            cancellationToken);

        var rows = (AsArray(response) ?? [])
            .Select(row => AsArray(row))
            .Where(row => row is not null)
            .Select(row => MapRow(client, row!, capturedUtc))
            .ToArray();

        // #878, #1356: the tracker's own message is d.message, which the list above already carries (MapRow keeps it
        // as ErrorMessage). rTorrent 0.16 has no t.message, so a t.multicall for it faulted every time and the
        // tracker status stayed Unknown. The shared phrase matcher decides whether the message means unregistered.
        var queue = rows
            .Select(item => item with
            {
                TrackerStatus = DownloadClientHelpers.LooksUnregistered(item.ErrorMessage)
                    ? DownloadTrackerStatuses.Unregistered
                    : DownloadTrackerStatuses.Ok
            })
            .Select(item => DownloadClientHelpers.ReconcileTorrentStatus(item, capturedUtc))
            .ToArray();

        string? clientVersion = null;
        try
        {
            // #1117: system.client_version answers the plain version, "0.9.8". No release feed is compared with it.
            clientVersion = Deluno.Connections.Versions.ConnectionVersionParsers.Clean(AsString(await SendAsync(http, baseUri, "system.client_version", [], cancellationToken)));
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or TaskCanceledException or System.Xml.XmlException or RtorrentFaultException)
        {
            // Unknown, not an error.
        }

        return CreateSnapshot(client, queue, capturedUtc, "healthy", $"Connected to rTorrent at {baseUri.Host}:{baseUri.Port}.", clientVersion: clientVersion);
    }

    private DownloadQueueItem MapRow(DownloadClientItem client, IReadOnlyList<object?> row, DateTimeOffset capturedUtc)
    {
        var hash = AsString(Field(row, 0)) ?? Guid.CreateVersion7().ToString("N");
        var name = AsString(Field(row, 1)) ?? "Unknown rTorrent item";
        var isActive = AsBool(Field(row, 2));
        var isHashChecking = AsBool(Field(row, 3));
        var complete = AsBool(Field(row, 4));
        var bytesDone = AsLong(Field(row, 5)) ?? 0;
        var sizeBytes = AsLong(Field(row, 6)) ?? 0;
        var downRate = AsLong(Field(row, 7)) ?? 0;
        var upRate = AsLong(Field(row, 8)) ?? 0;
        // rTorrent's own d.ratio is scaled by 1000 (1500 == 1.5).
        var ratio = AsLong(Field(row, 9)) is { } scaledRatio ? scaledRatio / 1000d : (double?)null;
        var message = AsString(Field(row, 10));
        var directory = AsString(Field(row, 11));
        // d.peers_connected counts every connected peer, seeds included (#1198).
        var peers = (int)Math.Clamp(AsLong(Field(row, 12)) ?? 0, 0, int.MaxValue);
        var progress = sizeBytes <= 0 ? 0 : (bytesDone / (double)sizeBytes * 100);

        var nativeStatus = isHashChecking ? "hash_checking"
            : !string.IsNullOrWhiteSpace(message) ? "error"
            : complete ? "complete"
            : !isActive ? "paused"
            : "downloading";

        return new DownloadQueueItem(
            hash, client.Id, client.Name, client.Protocol,
            DownloadClientHelpers.InferMediaType(client, null),
            DownloadClientHelpers.CleanReleaseTitle(name), name, string.Empty,
            NormalizeStatus(nativeStatus, progress), progress,
            (downRate / 1_000_000d), 0, sizeBytes, bytesDone, peers, "rTorrent",
            string.IsNullOrWhiteSpace(message) ? null : message,
            capturedUtc, Deluno.Contracts.SafePath.ReportedPath(directory),
            Ratio: ratio,
            UploadSpeedMbps: (upRate / 1_000_000d),
            // rTorrent's own message field is its own diagnostic text (a
            // tracker error included), so — like Deluge's equivalent field —
            // it is read as this machine's problem rather than the
            // release's, the same #880/#881 distinction every torrent
            // adapter here draws.
            IsSetupProblem: !string.IsNullOrWhiteSpace(message));
    }

    /// <summary>
    /// rTorrent's own <c>f.multicall(hash, "", "f.path=")</c> (#816, and the
    /// last of #803's own acceptance lines: a file listing for rTorrent) --
    /// every file's own path relative to the torrent's base directory, the
    /// same per-file granularity qBittorrent's and Deluge's file listings
    /// give. Null when the address or hash is missing, or the call itself
    /// fails, so the content blocklist falls back to the release name.
    /// </summary>
    public override async Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null || string.IsNullOrWhiteSpace(queueItemId)) return null;
        var http = CreateHttp(client);

        try
        {
            var response = await SendAsync(http, baseUri, "f.multicall", [queueItemId, string.Empty, "f.path="], cancellationToken);
            return (AsArray(response) ?? [])
                .Select(AsArray)
                .Where(row => row is not null && row.Count > 0)
                .Select(row => AsString(Field(row!, 0)))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
                .ToArray();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or RtorrentFaultException)
        {
            // A download rTorrent no longer holds answers a fault ("info-hash not found"): no listing, not an error.
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

    /// <summary>Whether rTorrent's <c>d.is_active</c> says the download is not running (#1251): 0 is paused or stopped.</summary>
    internal static bool IsPaused(long isActive) => isActive == 0;

    private async Task<DownloadClientActionResult> ExecuteActionCoreAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
    {
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.MissingAddress(client, queueItemId, action);
        var http = CreateHttp(client);

        // #1251: a pause or a resume is done only when rTorrent had the download before and shows it in the asked state after.
        if (action is DownloadClientActions.Pause or DownloadClientActions.Resume)
        {
            return await ClientPauseGuard.RunAsync(client, queueItemId, action, "rTorrent",
                async () =>
                {
                    try
                    {
                        var active = await SendAsync(http, baseUri, "d.is_active", [queueItemId], cancellationToken);
                        return AsLong(active) is { } value
                            ? new ClientPauseReading(ClientHas.Yes, IsPaused(value))
                            : new ClientPauseReading(ClientHas.Unknown, false);
                    }
                    catch (RtorrentFaultException)
                    {
                        // rTorrent answers a hash it does not hold with a fault.
                        return new ClientPauseReading(ClientHas.No, false);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException && !ClientCredentials.IsRefusal(exception) && exception is not ClientCredentialsRefusedException)
                    {
                        return new ClientPauseReading(ClientHas.Unknown, false);
                    }
                },
                () => RemoveAsync(client, http, baseUri, action, queueItemId, cancellationToken),
                ClientPauseGuard.SettleWindow, ClientPauseGuard.SettlePoll, cancellationToken);
        }

        // #1252: a removal is done only when rTorrent had the download before and no longer has it after (#1212).
        if (DownloadClientActions.RemovesTheDownload(action))
        {
            return await ClientRemovalGuard.RunAsync(client, queueItemId, action, "rTorrent",
                async () =>
                {
                    try
                    {
                        await SendAsync(http, baseUri, "d.name", [queueItemId], cancellationToken);
                        return ClientHas.Yes;
                    }
                    catch (RtorrentFaultException)
                    {
                        // rTorrent answers a hash it does not hold with a fault.
                        return ClientHas.No;
                    }
                    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException && !ClientCredentials.IsRefusal(exception))
                    {
                        return ClientHas.Unknown;
                    }
                },
                () => RemoveAsync(client, http, baseUri, action, queueItemId, cancellationToken),
                ClientRemovalGuard.TorrentSettleWindow, ClientRemovalGuard.TorrentSettlePoll, cancellationToken);
        }

        return await RemoveAsync(client, http, baseUri, action, queueItemId, cancellationToken);
    }

    private async Task<DownloadClientActionResult> RemoveAsync(
        DownloadClientItem client, HttpClient http, Uri baseUri, string action, string queueItemId, CancellationToken cancellationToken)
    {
        if (action == DownloadClientActions.DeleteWithData)
        {
            return await DeleteWithDataAsync(client, http, baseUri, queueItemId, cancellationToken);
        }

        // Delete and RemoveRefusedCopy both reach plain d.erase, for the reason this
        // class's own doc comment gives: rTorrent's RPC has nothing further
        // either could call.
        var method = action switch
        {
            DownloadClientActions.Pause => "d.pause",
            DownloadClientActions.Resume => "d.resume",
            DownloadClientActions.Recheck => "d.check_hash",
            DownloadClientActions.Delete => "d.erase",
            DownloadClientActions.RemoveRefusedCopy => "d.erase",
            _ => null
        };
        if (method is null) return DownloadClientHelpers.Unsupported(client, queueItemId, action, "rTorrent");

        try
        {
            await SendAsync(http, baseUri, method, [queueItemId], cancellationToken);
            return DownloadClientHelpers.ActionSuccess(client, queueItemId, action, "rTorrent action sent.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException && !ClientCredentials.IsRefusal(exception))
        {
            return DownloadClientHelpers.ActionFailure(client, queueItemId, action, exception.Message, null);
        }
    }

    /// <summary>
    /// rTorrent has no "delete with data" of its own (see this class's doc
    /// comment): Deluno asks rTorrent for the download's own path, releases
    /// rTorrent's hold on it with <c>d.erase</c>, and deletes exactly that path
    /// itself -- only when the path is one it can safely say belongs to this
    /// download alone, never a guess. Anything less than that is a limit
    /// stated on screen, not a silent no-op.
    /// </summary>
    private async Task<DownloadClientActionResult> DeleteWithDataAsync(
        DownloadClientItem client,
        HttpClient http,
        Uri baseUri,
        string queueItemId,
        CancellationToken cancellationToken)
    {
        string? path;
        try
        {
            path = AsString(await SendAsync(http, baseUri, "d.base_path", [queueItemId], cancellationToken));
            if (string.IsNullOrWhiteSpace(path))
            {
                path = AsString(await SendAsync(http, baseUri, "d.directory", [queueItemId], cancellationToken));
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            return DownloadClientHelpers.ActionFailure(client, queueItemId, DownloadClientActions.DeleteWithData, $"Could not ask rTorrent where this download's files are: {exception.Message}", null);
        }

        try
        {
            await SendAsync(http, baseUri, "d.erase", [queueItemId], cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            return DownloadClientHelpers.ActionFailure(client, queueItemId, DownloadClientActions.DeleteWithData, exception.Message, null);
        }

        if (IsSafeToDeleteRtorrentPath(path, out var safePath)
            && DownloadFolderRemoval.LibraryRefusal(await DownloadFolderRemoval.ReadLibrariesAsync(libraries, cancellationToken), safePath) is { } refusal)
        {
            // #1328: a path the client reports that is, holds or sits inside a library root is never deleted.
            return DownloadClientHelpers.ActionSuccess(client, queueItemId, DownloadClientActions.DeleteWithData,
                $"rTorrent has no delete-with-data of its own. Deluno removed it from rTorrent but left its files on disk. {refusal}")
                with { FilesNote = refusal };
        }

        if (!IsSafeToDeleteRtorrentPath(path, out var fullPath))
        {
            return DownloadClientHelpers.ActionSuccess(
                client,
                queueItemId,
                DownloadClientActions.DeleteWithData,
                "rTorrent has no delete-with-data of its own. Deluno removed it from rTorrent, but could not safely find its files on this machine, so they were left on disk -- remove them by hand.")
                with { FilesNote = "Deluno could not safely find its files on this machine." };
        }

        try
        {
            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
            else
            {
                File.Delete(fullPath);
            }

            return DownloadClientHelpers.ActionSuccess(
                client,
                queueItemId,
                DownloadClientActions.DeleteWithData,
                $"rTorrent has no delete-with-data of its own. Deluno removed it from rTorrent and deleted its files at {fullPath} itself.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DownloadClientHelpers.ActionSuccess(
                client,
                queueItemId,
                DownloadClientActions.DeleteWithData,
                $"rTorrent has no delete-with-data of its own. Deluno removed it from rTorrent, but could not delete its files at {fullPath}: {exception.Message}")
                with { FilesNote = $"Deluno could not delete {fullPath}: {exception.Message}" };
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is one Deluno can safely delete on this
    /// download's behalf: named, resolvable, not a filesystem root (rTorrent
    /// reporting "/" or "C:\" is a misconfiguration, not this download's own
    /// folder), and actually present on this machine -- a remote rTorrent
    /// whose files Deluno cannot see reports a path that simply does not
    /// exist here, which is exactly the case this must not guess through.
    /// </summary>
    private static bool IsSafeToDeleteRtorrentPath(string? path, out string fullPath)
        => DownloadFolderRemoval.IsSafeToDelete(path, out fullPath);

    public override string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
    {
        var normalized = nativeStatus?.ToLowerInvariant() ?? string.Empty;
        if (normalized == "hash_checking") return DownloadQueueStatuses.NotTransferring;
        if (normalized == "error") return DownloadQueueStatuses.Stalled;
        if (Deluno.Platform.Contracts.DownloadHealthStrikes.IsFinishedPercent(progress) || normalized == "complete") return DownloadQueueStatuses.ImportReady;
        if (normalized == "paused") return DownloadQueueStatuses.Paused;
        return DownloadQueueStatuses.Downloading;
    }

    /// <summary>
    /// #1240: sets the torrent's label with <c>d.custom1.set</c> (the field ruTorrent and Radarr call the label) and
    /// nothing else. rTorrent has no category list to check against, so a label is simply set.
    /// </summary>
    public override async Task<DownloadClientActionResult> SetCategoryAsync(
        DownloadClientItem client, string queueItemId, string category, string? currentCategory, CancellationToken cancellationToken)
    {
        var action = DownloadClientActions.SetCategory;
        var baseUri = DownloadClientHelpers.ResolveEndpoint(client);
        if (baseUri is null) return DownloadClientHelpers.MissingAddress(client, queueItemId, action);
        var http = CreateHttp(client);
        var label = category.Trim();
        return await ClientCategoryMove.RunAsync(
            client, queueItemId, label, "rTorrent",
            async () =>
            {
                try
                {
                    return new ClientCategoryReading(ClientHas.Yes, AsString(await SendAsync(http, baseUri, "d.custom1", [queueItemId], cancellationToken)));
                }
                catch (RtorrentFaultException)
                {
                    // rTorrent answers a hash it does not hold with a fault.
                    return new ClientCategoryReading(ClientHas.No, null);
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
                {
                    return new ClientCategoryReading(ClientHas.Unknown, null);
                }
            },
            async _ =>
            {
                try
                {
                    await SendAsync(http, baseUri, "d.custom1.set", [queueItemId, label], cancellationToken);
                    return DownloadClientHelpers.ActionSuccess(client, queueItemId, action, "rTorrent action sent.");
                }
                catch (RtorrentFaultException fault)
                {
                    return DownloadClientHelpers.ActionFailure(client, queueItemId, action, $"rTorrent refused to set the label: {fault.FaultMessage}.");
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
                {
                    return DownloadClientHelpers.ActionFailure(client, queueItemId, action, exception.Message, null);
                }
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
                name.Length == 0 ? "Enter a category before checking it." : "Add the rTorrent address before checking a category.", Supported: true, Found: false);
        }

        try
        {
            await SendAsync(CreateHttp(client), baseUri, "system.client_version", [], cancellationToken);
            return new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Ready,
                $"rTorrent does not list its labels, so Deluno can't check this one. It sets the {name} label when it moves a download.", Supported: true, Found: false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or RtorrentFaultException)
        {
            return new(client.Id, client.Name, name, DownloadClientCategoryStatuses.Unreachable,
                $"Deluno could not reach rTorrent: {exception.Message}", Supported: true, Found: false);
        }
    }

    private HttpClient CreateHttp(DownloadClientItem client)
    {
        var http = httpClientFactory.CreateClient("download-clients");
        http.Timeout = TimeSpan.FromSeconds(8);
        if (!string.IsNullOrWhiteSpace(client.Username) || !string.IsNullOrWhiteSpace(client.Secret))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{client.Username}:{client.Secret}"));
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", token);
        }

        return http;
    }

    /// <summary>download_list with no view name -- every hash rTorrent currently holds.</summary>
    private static async Task<HashSet<string>?> ListHashesAsync(HttpClient http, Uri baseUri, CancellationToken cancellationToken)
    {
        try
        {
            var result = await SendAsync(http, baseUri, "download_list", [], cancellationToken);
            return (AsArray(result) ?? [])
                .Select(AsString)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The same bounded wait qBittorrent's adapter uses (#606): load.start
    /// answers as soon as rTorrent has accepted the job, before the hash
    /// necessarily appears in download_list, so asking once immediately is
    /// asking before the answer exists.
    /// </summary>
    private static async Task<HashSet<string>?> WaitForNewHashAsync(HttpClient http, Uri baseUri, HashSet<string>? before, string? knownHash, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(6);
        HashSet<string>? after;
        while (true)
        {
            after = await ListHashesAsync(http, baseUri, cancellationToken);
            if (before is null || after is null || (knownHash is null ? after.Except(before).Any() : after.Contains(knownHash, StringComparer.OrdinalIgnoreCase))) return after;
            if (DateTimeOffset.UtcNow >= deadline) return after;
            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        }
    }

    // ── Minimal XML-RPC codec ────────────────────────────────────────────────
    // rTorrent's RPC is the one protocol here that is not JSON, so unlike
    // every other adapter this cannot reuse DownloadClientHelpers.PostJsonAsync.
    // Deliberately minimal: only the value shapes rTorrent's own methods
    // actually return (string, integer, boolean, array — no struct, which
    // rTorrent's XML-RPC responses never use).

    private static async Task<object?> SendAsync(HttpClient http, Uri baseUri, string method, IReadOnlyList<object> parameters, CancellationToken cancellationToken)
    {
        var requestXml = BuildRequest(method, parameters);
        using var content = new StringContent(requestXml, Encoding.UTF8, "text/xml");
        using var response = await http.PostAsync(baseUri, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        var responseXml = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseResponse(responseXml);
    }

    private static string BuildRequest(string method, IReadOnlyList<object> parameters)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\"?><methodCall><methodName>").Append(method).Append("</methodName><params>");
        foreach (var parameter in parameters)
        {
            builder.Append("<param>").Append(EncodeValue(parameter)).Append("</param>");
        }

        builder.Append("</params></methodCall>");
        return builder.ToString();
    }

    private static string EncodeValue(object value) => value switch
    {
        string text => $"<value><string>{System.Security.SecurityElement.Escape(text)}</string></value>",
        int number => $"<value><i4>{number.ToString(CultureInfo.InvariantCulture)}</i4></value>",
        long number => $"<value><i4>{number.ToString(CultureInfo.InvariantCulture)}</i4></value>",
        bool flag => $"<value><boolean>{(flag ? 1 : 0)}</boolean></value>",
        XmlRpcBase64 raw => $"<value><base64>{Convert.ToBase64String(raw.Bytes)}</base64></value>",
        _ => $"<value><string>{System.Security.SecurityElement.Escape(value.ToString() ?? string.Empty)}</string></value>"
    };

    /// <summary>Raw bytes for an XML-RPC &lt;base64&gt; value, distinct from a string parameter.</summary>
    private sealed record XmlRpcBase64(byte[] Bytes);

    /// <summary>
    /// rTorrent's own XML-RPC &lt;fault&gt; response — the method it was
    /// asked to run could not be honoured, in the same request, with its own
    /// stated reason (#874/#942).
    /// </summary>
    private sealed class RtorrentFaultException(string faultMessage) : Exception($"rTorrent returned a fault: {faultMessage}")
    {
        public string FaultMessage { get; } = faultMessage;
    }

    private static object? ParseResponse(string xml)
    {
        var document = XDocument.Parse(xml);
        var fault = document.Root?.Element("fault");
        if (fault is not null)
        {
            var faultValue = fault.Element("value");
            var faultString = faultValue is not null ? ParseValue(faultValue) : null;
            throw new RtorrentFaultException(faultString?.ToString() ?? "no reason given");
        }

        var valueElement = document.Root?.Element("params")?.Element("param")?.Element("value");
        return valueElement is null ? null : ParseValue(valueElement);
    }

    private static object? ParseValue(XElement valueElement)
    {
        var typed = valueElement.Elements().FirstOrDefault();
        // No type element at all means a bare string, per the XML-RPC spec.
        if (typed is null) return valueElement.Value;

        return typed.Name.LocalName switch
        {
            "string" => typed.Value,
            "int" or "i4" or "i8" => long.TryParse(typed.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0L,
            "boolean" => typed.Value.Trim() is "1" or "true",
            "double" => double.TryParse(typed.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) ? real : 0d,
            "array" => (typed.Element("data")?.Elements("value") ?? []).Select(ParseValue).ToList(),
            _ => typed.Value
        };
    }

    private static List<object?>? AsArray(object? value) => value as List<object?>;
    private static string? AsString(object? value) => value as string;
    private static long? AsLong(object? value) => value switch { long l => l, int i => i, _ => null };
    private static bool AsBool(object? value) => value switch { bool b => b, long l => l != 0, _ => false };
    private static object? Field(IReadOnlyList<object?> row, int index) => index < row.Count ? row[index] : null;
}
