using System.Globalization;
using Deluno.Infrastructure.Http;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Integrations.DownloadClients;

internal static class DownloadClientHelpers
{
    private static readonly string[] UsenetProtocols = ["sabnzbd", "nzbget"];

    /// <summary>Whether a client protocol is Usenet rather than BitTorrent - Usenet has no peers, seeders, or swarms (#905).</summary>
    internal static bool IsUsenetProtocol(string protocol)
        => UsenetProtocols.Contains(protocol, StringComparer.OrdinalIgnoreCase);

    internal static Uri? ResolveEndpoint(DownloadClientItem client)
    {
        if (!string.IsNullOrWhiteSpace(client.EndpointUrl) &&
            Uri.TryCreate(EnsureTrailingSlash(client.EndpointUrl), UriKind.Absolute, out var endpoint))
        {
            return endpoint;
        }

        if (string.IsNullOrWhiteSpace(client.Host)) return null;
        // A host already written as a full URL ("http://host") keeps whatever
        // scheme it names; UseSsl only decides the scheme Deluno has to guess.
        var scheme = client.Host.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : client.UseSsl ? "https://" : "http://";
        var port = client.Port is > 0 ? $":{client.Port}" : string.Empty;
        var urlBase = string.IsNullOrWhiteSpace(client.UrlBase) ? string.Empty : $"/{client.UrlBase.Trim('/')}";
        return Uri.TryCreate(EnsureTrailingSlash($"{scheme}{client.Host}{port}{urlBase}"), UriKind.Absolute, out var uri) ? uri : null;
    }

    internal static string BuildResilienceKey(DownloadClientItem client, string purpose)
    {
        var endpoint = ResolveEndpoint(client);
        var address = endpoint is null
            ? "unconfigured"
            : $"{endpoint.Scheme}://{endpoint.Host}:{endpoint.Port}{endpoint.AbsolutePath.TrimEnd('/')}";
        return $"download-client:{client.Id}:{client.Protocol}:{purpose}:{address}";
    }

    internal static string ResolveCategory(DownloadClientItem client, DownloadClientGrabRequest request)
        => !string.IsNullOrWhiteSpace(request.Category)
            ? request.Category
            : request.MediaType == "tv"
                ? client.TvCategory ?? client.CategoryTemplate ?? "tv"
                : client.MoviesCategory ?? client.CategoryTemplate ?? "movies";

    internal static string InferMediaType(DownloadClientItem client, string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return "movies";
        if (!string.IsNullOrWhiteSpace(client.TvCategory) && string.Equals(category, client.TvCategory, StringComparison.OrdinalIgnoreCase)) return "tv";
        if (!string.IsNullOrWhiteSpace(client.MoviesCategory) && string.Equals(category, client.MoviesCategory, StringComparison.OrdinalIgnoreCase)) return "movies";
        var normalized = category.Trim().ToLowerInvariant();
        return normalized.Contains("sonarr") || normalized.Contains("series") || normalized.Contains("show") || normalized.Contains("tv") ? "tv" : "movies";
    }

    internal static string EnsureTrailingSlash(string value) => value.EndsWith('/') ? value : $"{value}/";

    internal static string BuildQuery(IEnumerable<KeyValuePair<string, string>> values)
        => string.Join("&", values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    internal static void AddBasicAuth(HttpClient http, DownloadClientItem client)
    {
        if (string.IsNullOrWhiteSpace(client.Username) && string.IsNullOrWhiteSpace(client.Secret)) return;
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{client.Username ?? string.Empty}:{client.Secret ?? string.Empty}"));
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", raw);
    }

    internal static NetworkCredential? BuildCredential(DownloadClientItem client)
        => string.IsNullOrWhiteSpace(client.Username) && string.IsNullOrWhiteSpace(client.Secret)
            ? null
            : new NetworkCredential(client.Username ?? string.Empty, client.Secret ?? string.Empty);

    internal static async Task<string> GetUTorrentTokenAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var html = await http.GetStringAsync("gui/token.html", cancellationToken);
        var start = html.IndexOf('>');
        var end = html.LastIndexOf('<');
        return start >= 0 && end > start ? html[(start + 1)..end].Trim() : string.Empty;
    }

    internal static async Task<T?> PostJsonAsync<T>(HttpClient http, Uri uri, object payload, CancellationToken cancellationToken)
    {
        using var response = await http.PostSizedJsonAsync(uri, payload, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    /// <summary>
    /// The torrent's info-hash for this grab, known before the client was asked
    /// anything (#1062): the one the grab service worked out, or, for a caller
    /// that talks to a client directly, the one the magnet or the file names.
    /// </summary>
    internal static string? KnownHashOf(DownloadClientGrabRequest request)
        => request.KnownInfoHash
           ?? TorrentInfoHash.FromMagnet(request.DownloadUrl)
           ?? (request.TorrentFile is { } file ? TorrentInfoHash.FromTorrentFile(file, out _) : null);

    /// <summary>
    /// When the hash was not known beforehand, the one torrent that appeared in
    /// the client's list because of this add. Two or more new ones (something
    /// else was added at the same moment) name nothing, and no torrent is
    /// ever picked out of a diff by position.
    /// </summary>
    internal static string? TheOnlyNewItem(IReadOnlyCollection<string>? before, IReadOnlyCollection<string>? after)
    {
        if (before is null || after is null)
        {
            return null;
        }

        var added = after.Except(before, StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        return added.Length == 1 ? added[0] : null;
    }

    internal static DownloadClientGrabResult GrabSuccess(DownloadClientItem client, DownloadClientGrabRequest request, string message)
        => new(client.Id, request.ReleaseName, true, "sent", message);

    internal static DownloadClientGrabResult GrabFailure(DownloadClientItem client, DownloadClientGrabRequest request, string message)
        => new(client.Id, request.ReleaseName, false, "failed", message)
        {
            Failure = IntegrationFailureFactory.FromLegacy(
                "download-client",
                client.Id,
                client.Name,
                "grab",
                "configuration",
                message)
        };

    /// <summary>
    /// Distinct from <see cref="GrabFailure"/>'s "configuration": nothing here
    /// is a setup problem, it is the client saying outright that the release
    /// it was handed is unusable (#876) — a dead link or a file it could not
    /// parse. The caller (a search handler) blocklists the release and lets
    /// the next search pick a different one, instead of waiting for its
    /// ordinary retry cycle to fail the same way again.
    /// </summary>
    internal static DownloadClientGrabResult DeadReleaseGrabFailure(DownloadClientItem client, DownloadClientGrabRequest request, string message)
        => new(client.Id, request.ReleaseName, false, DownloadQueueStatuses.DeadRelease, message)
        {
            Failure = IntegrationFailureFactory.FromLegacy(
                "download-client",
                client.Id,
                client.Name,
                "grab",
                "rejected",
                message)
        };

    internal static DownloadClientActionResult MissingAddress(DownloadClientItem client, string queueItemId, string action)
        => new(client.Id, queueItemId, action, false, "Client address is missing.")
        {
            Failure = IntegrationFailureFactory.FromLegacy(
                "download-client",
                client.Id,
                client.Name,
                $"action:{action}",
                "configuration",
                "Client address is missing.")
        };

    internal static DownloadClientActionResult Unsupported(DownloadClientItem client, string queueItemId, string action, string label)
        => new(client.Id, queueItemId, action, false, $"{label} does not support this action.")
        {
            Failure = IntegrationFailureFactory.FromLegacy(
                "download-client",
                client.Id,
                client.Name,
                $"action:{action}",
                "rejected",
                $"{label} does not support this action.")
        };

    internal static DownloadClientActionResult ActionFailure(
        DownloadClientItem client,
        string queueItemId,
        string action,
        string message,
        HttpStatusCode? statusCode = null,
        string? upstreamDetail = null,
        string category = "rejected")
        => new(client.Id, queueItemId, action, false, message)
        {
            Failure = statusCode is { } status
                ? IntegrationFailureFactory.FromHttpStatus(
                    "download-client",
                    client.Id,
                    client.Name,
                    $"action:{action}",
                    status,
                    message,
                    upstreamDetail)
                : IntegrationFailureFactory.FromLegacy(
                    "download-client",
                    client.Id,
                    client.Name,
                    $"action:{action}",
                    category,
                    message,
                    upstreamDetail: upstreamDetail)
        };

    internal static DownloadClientActionResult ActionSuccess(DownloadClientItem client, string queueItemId, string action, string message)
        => new(client.Id, queueItemId, action, true, message);

    /// <summary>
    /// A connection failure in the operating system's own words ("No connection could be made because the target
    /// machine actively refused it") means whatever was called is not answering. Said that way, naming who, instead
    /// of passing the raw text to a screen (golden path rc.30.9). Null when the text is about something else.
    /// </summary>
    internal static string? PlainConnectionFailure(string? raw, string whoIsNotAnswering)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string[] markers =
        [
            "actively refused", "no connection could be made", "connection refused", "connection was refused",
            "network is unreachable", "no such host is known", "name or service not known", "host is unreachable"
        ];
        return markers.Any(marker => raw.Contains(marker, StringComparison.OrdinalIgnoreCase))
            ? $"{whoIsNotAnswering} isn't answering."
            : null;
    }

    /// <summary>
    /// Adapters historically returned a failed queue row with only a native
    /// status or error string. Normalize that boundary once so every caller
    /// receives an attributable failure instead of having to infer it again.
    /// </summary>
    internal static DownloadQueueItem NormalizeQueueFailure(DownloadQueueItem item)
    {
        if (item.Failure is not null || (!IsFailureStatus(item.Status) && string.IsNullOrWhiteSpace(item.ErrorMessage)))
        {
            return item;
        }

        var message = string.IsNullOrWhiteSpace(item.ErrorMessage)
            ? $"The download client reported queue status '{item.Status}'."
            : PlainConnectionFailure(item.ErrorMessage, item.ClientName) ?? item.ErrorMessage.Trim();
        return item with
        {
            ErrorMessage = string.IsNullOrWhiteSpace(item.ErrorMessage) ? item.ErrorMessage : message,
            Failure = IntegrationFailureFactory.FromLegacy(
                "download-client",
                item.ClientId,
                item.ClientName,
                "queue",
                "failed",
                message,
                code: item.Status,
                externalId: item.Id)
        };
    }

    /// <summary>Backfills the same typed contract for native client history.</summary>
    internal static DownloadClientHistoryItem NormalizeHistoryFailure(
        DownloadClientHistoryItem item)
    {
        if (item.Failure is not null || (!IsFailureStatus(item.Outcome) && string.IsNullOrWhiteSpace(item.ErrorMessage)))
        {
            return item;
        }

        var message = string.IsNullOrWhiteSpace(item.ErrorMessage)
            ? $"The download client reported history outcome '{item.Outcome}'."
            : item.ErrorMessage.Trim();
        return item with
        {
            Failure = IntegrationFailureFactory.FromLegacy(
                "download-client",
                item.ClientId,
                item.ClientName,
                "history",
                "failed",
                message,
                code: item.Outcome,
                externalId: item.ExternalId ?? item.Id)
        };
    }

    internal static DownloadClientTelemetrySnapshot NormalizeSnapshotFailures(
        DownloadClientTelemetrySnapshot snapshot)
    {
        var queue = snapshot.Queue
            .Select(NormalizeQueueFailure)
            .ToArray();
        return snapshot with
        {
            Queue = queue,
            Summary = DownloadQueueSummary.Of(queue),
            History = snapshot.History
                .Select(NormalizeHistoryFailure)
                .ToArray()
        };
    }

    internal static bool IsFailureStatus(string? status)
    {
        var normalized = status?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized.Contains("fail", StringComparison.Ordinal)
            || normalized.Contains("error", StringComparison.Ordinal);
    }

    /// <summary>
    /// The wording trackers actually use to say a torrent is gone for good
    /// (#878, cleanup sheet C5) — different tracker software phrases it
    /// differently, but each of these means "no announce will ever succeed
    /// again", which is a different fact from the tracker being merely
    /// unreachable right now (a timeout or connection-refused message matches
    /// none of these).
    /// </summary>
    private static readonly string[] UnregisteredTrackerPhrases =
    [
        "unregistered", "not registered", "torrent not found", "not authorized",
        "torrent has been deleted", "info hash not found"
    ];

    /// <summary>Whether a tracker's own message is it declaring the torrent unregistered/removed (#878).</summary>
    internal static bool LooksUnregistered(string? trackerMessage)
        => !string.IsNullOrWhiteSpace(trackerMessage) &&
           UnregisteredTrackerPhrases.Any(phrase => trackerMessage.Contains(phrase, StringComparison.OrdinalIgnoreCase));

    internal static string CleanReleaseTitle(string value) => value.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ').Trim();

    internal static string? ChoosePath(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    internal static string? ResolveDownloadPath(string? directory, string? name)
    {
        var cleanDirectory = directory?.Trim();
        var cleanName = name?.Trim();
        if (string.IsNullOrWhiteSpace(cleanDirectory)) return null;
        if (string.IsNullOrWhiteSpace(cleanName)) return cleanDirectory;
        if (cleanName.Contains('\\') || cleanName.Contains('/')) return cleanName;
        var separator = cleanDirectory.Contains('\\') ? "\\" : "/";
        return cleanDirectory.EndsWith('\\') || cleanDirectory.EndsWith('/') ? $"{cleanDirectory}{cleanName}" : $"{cleanDirectory}{separator}{cleanName}";
    }

    internal static DateTimeOffset FromUnix(long? value) => value is > 0 ? DateTimeOffset.FromUnixTimeSeconds(value.Value) : DateTimeOffset.UtcNow;

    /// <summary>
    /// #811/#973: a "not yet" reading (qBittorrent's -1/0 for no activity or
    /// completion, Transmission's 0 for a doneDate/activityDate that has
    /// never been set) is null, not the Unix epoch - shared by every client
    /// adapter that reads one of these optional timestamps.
    /// </summary>
    internal static DateTimeOffset? FromUnixOrNull(long? value)
        => value is > 0 ? DateTimeOffset.FromUnixTimeSeconds(value.Value) : null;

    internal static int ParseId(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    /// <summary>
    /// The same parse, for the clients that address downloads by number, but
    /// admitting when it cannot.
    ///
    /// <para><see cref="ParseId"/> answers 0 for anything it cannot read, and 0
    /// is a torrent id — so an id in the wrong shape produced a well-formed
    /// request aimed at nothing, which the client accepted and Deluno reported
    /// as done. Silence in the shape of success. Transmission and NZBGet use
    /// this instead and refuse the action outright.</para>
    /// </summary>
    internal static bool TryParseId(string value, out int id)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);

    internal static DownloadClientActionResult UnreadableId(DownloadClientItem client, string queueItemId, string action, string label)
        => ActionFailure(
            client,
            queueItemId,
            action,
            $"\"{queueItemId}\" is not a download id {label} would recognise, so Deluno did not send the request.",
            upstreamDetail: $"{label} addresses downloads by number.",
            category: "configuration");

    /// <summary>
    /// The one rule every torrent adapter (Transmission, qBittorrent, Deluge)
    /// applies to a freshly built <see cref="DownloadQueueItem"/> before it
    /// reaches a screen, so none of them has to reimplement it (#650).
    ///
    /// <para><b>Never complete on "nothing left" alone.</b> A magnet whose
    /// metadata has not arrived reports zero size, zero progress and zero
    /// peers — on Transmission that is <c>leftUntilDone == 0</c> only because
    /// <c>sizeWhenDone</c> is still unknown, not because the transfer is
    /// done. A client-reported "done" status is trusted only once the size is
    /// actually known (greater than zero); otherwise a status an adapter
    /// mapped to <see cref="DownloadQueueStatuses.ImportReady"/> or
    /// <see cref="DownloadQueueStatuses.Completed"/> is downgraded back to
    /// downloading.</para>
    ///
    /// <para><b>No metadata and no peers, for the stall window, is stalled.</b>
    /// #618 gave Deluge this rule for the same reason a torrent with no
    /// connected peers never gets a chance to show throughput: the generic
    /// no-throughput health check needs at least one sample of "not moving",
    /// and a torrent that never even resolves its own size can sit at
    /// "downloading" forever without ever tripping it. qBittorrent's own
    /// <c>metaDL</c> is exactly this case (its <c>stalledDL</c> already maps
    /// to <see cref="DownloadQueueStatuses.Stalled"/> through its status
    /// text), so this is the one place all three clients get the same
    /// answer.</para>
    ///
    /// <para><b>Known metadata and connected peers can still mean nothing is
    /// arriving (#659).</b> Peers that never send anything are not evidence
    /// of progress; only a gain in <see cref="DownloadQueueItem.DownloadedBytes"/>
    /// between observations is. <paramref name="lastProgressUtc"/> is that
    /// evidence - when this item's bytes were last seen to increase, kept in
    /// <see cref="Deluno.Platform.Data.IDownloadHealthRepository.RecordDownloadProgressAsync"/>
    /// so a restart does not read as fresh progress. No gain for the stall
    /// window is stalled regardless of peer count; any gain moves the clock,
    /// so a slow-but-moving torrent is never caught here. Null means nothing
    /// has been persisted yet (a caller that has not wired up progress
    /// tracking, or this item's first sighting), which this rule treats the
    /// same as "not stalled yet" rather than guessing.</para>
    ///
    /// <para><paramref name="noProgressWindow"/> is the configurable half of
    /// that second check (#659's later scope: a person's own "no progress"
    /// minutes, off entirely when the caller has switched that rule off and
    /// leaves it null). It defaults to <see cref="DownloadHealthStrikes.DefaultWindow"/>
    /// for a caller with nothing configured, the same 30 minutes this rule
    /// always used. It does not touch the metadata-and-no-peers window above,
    /// which #618/#650 already covers and #659's settings screen never asked
    /// to change.</para>
    /// </summary>
    internal static DownloadQueueItem ReconcileTorrentStatus(
        DownloadQueueItem item,
        DateTimeOffset capturedUtc,
        DateTimeOffset? lastProgressUtc = null,
        TimeSpan? noProgressWindow = null)
    {
        var metadataKnown = item.SizeBytes > 0;

        if (!metadataKnown && item.Status is DownloadQueueStatuses.ImportReady or DownloadQueueStatuses.Completed)
        {
            return item with { Status = DownloadQueueStatuses.Downloading };
        }

        if (!metadataKnown &&
            item.Peers <= 0 &&
            item.Status == DownloadQueueStatuses.Downloading &&
            capturedUtc - item.AddedUtc >= DownloadHealthStrikes.DefaultWindow)
        {
            return item with { Status = DownloadQueueStatuses.Stalled };
        }

        // The one place a download below 100% becomes Stalled (#1193). A client's own "stalled" flag is not read as
        // Stalled by any adapter (it may only be closing its last blocks); only no byte gain for the whole window
        // is, at any percent. Moving, checking and complete downloads are not Downloading here, so are never struck.
        if (item.Status == DownloadQueueStatuses.Downloading &&
            item.Progress < 100d &&
            lastProgressUtc is { } since &&
            capturedUtc - since >= (noProgressWindow ?? DownloadHealthStrikes.DefaultWindow))
        {
            return item with { Status = DownloadQueueStatuses.Stalled };
        }

        return item;
    }

    /// <summary>The host of a tracker announce URL (#1194), lower case; null for anything that is not a URL, such as qBittorrent's "** [DHT] **" rows.</summary>
    internal static string? HostOf(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) ? uri.Host.ToLowerInvariant() : null;

    /// <summary>The distinct hosts of several announce URLs (#1194).</summary>
    internal static IReadOnlyList<string> HostsOf(IEnumerable<string?> urls)
        => urls.Select(HostOf).Where(host => host is not null).Select(host => host!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>A comma-separated tag list as a client reports it (#1194), trimmed, empty entries dropped.</summary>
    internal static IReadOnlyList<string> SplitTags(string? tags)
        => string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    internal static string NormalizeTextStatus(string? status, double? progress)
    {
        var normalized = status?.ToLowerInvariant() ?? string.Empty;

        // A client that says "error" is telling you something about the data
        // on disk, and it outranks the progress figure. This used to test
        // completion first, so a torrent sitting at 100% in Deluge's Error
        // state - a failed recheck, a move that could not finish - was
        // reported ImportReady and handed to the import pipeline as though
        // the client were happy with it. Deluge and uTorrent both normalise
        // through here, and neither passes an error code, so this text is the
        // only signal there is.
        if (normalized.Contains("error") || normalized.Contains("fail"))
        {
            return DownloadQueueStatuses.Stalled;
        }

        // A client's own "stalled" flag below 100% is not proof on its own (#1193): it reads Downloading until
        // ReconcileTorrentStatus finds no progress for Deluno's own window. The same rule every adapter follows.
        if (normalized.Contains("stalled") && (progress ?? 0) < 100d)
        {
            return DownloadQueueStatuses.Downloading;
        }

        // NZBGet's post-processing stages (#663) — checked ahead of the
        // "complete"/"queue" tests below, both of which would otherwise
        // misread one: a download stuck verifying or repairing for hours sits
        // at 100% progress, which would read as ImportReady, and
        // "PP_QUEUED" contains the literal word "queue". Neither is what a
        // person means by either state — this is past the transfer and short
        // of the library, the same "processing" NZBGet's own dispatch handoff
        // already uses for Deluno's cleanup step.
        if (IsNzbGetPostProcessingStage(normalized))
        {
            return DownloadQueueStatuses.Processing;
        }

        // Deluge (and any other client normalising through this shared text
        // path) reports its own "Checking" state — re-hashing a torrent, on a
        // restart or after the data folder moved — at whatever progress the
        // torrent already reached, including 100 for one already complete
        // (#912). Checked ahead of the progress-based ImportReady test below,
        // the same way qBittorrent's and uTorrent's own checking states are,
        // so a re-check never races the import against a file still being
        // verified.
        if (normalized.Contains("checking"))
        {
            return DownloadQueueStatuses.NotTransferring;
        }

        if (DownloadHealthStrikes.IsFinishedPercent(progress) || normalized.Contains("complete") || normalized.Contains("seeding")) return DownloadQueueStatuses.ImportReady;
        if (normalized.Contains("pause") || normalized.Contains("queue")) return DownloadQueueStatuses.Queued;
        return DownloadQueueStatuses.Downloading;
    }

    /// <summary>
    /// NZBGet's queue <c>Status</c> during PP_QUEUED, LOADING_PARS,
    /// VERIFYING_SOURCES, REPAIRING, VERIFYING_REPAIRED, RENAMING, UNPACKING
    /// and EXECUTING_SCRIPT (#663) — real work happening after the transfer
    /// finished, not a stalled or still-downloading item. Matched loosely
    /// against the substrings NZBGet's own uppercase constants contain, since
    /// this method only ever sees NZBGet's text on this path (see
    /// <see cref="NzbGetDownloadClient.NormalizeStatus"/>) — <c>MOVING</c> is
    /// the one word Deluge also uses, and Deluge never reaches this method
    /// while its own state is "Moving" (<c>DelugeDownloadClient.StillMoving</c>
    /// intercepts it first).
    /// </summary>
    private static bool IsNzbGetPostProcessingStage(string normalized) =>
        normalized.Contains("pp_queued") || normalized.Contains("loading_pars") ||
        normalized.Contains("verifying_sources") || normalized.Contains("verifying_repaired") ||
        normalized.Contains("repairing") || normalized.Contains("renaming") ||
        normalized.Contains("unpacking") || normalized.Contains("moving") ||
        normalized.Contains("executing_script");

    /// <summary>
    /// Low free disk space, folded into a client's health the same way any
    /// other degraded condition is (#663). Deliberately not its own alert
    /// mechanism: the client's health snapshot is re-derived on every poll, so
    /// this clears itself the moment space frees up, the same as a client
    /// coming back online — nothing to dismiss, nothing that can go stale.
    /// </summary>
    internal static (string Health, string? Message) EvaluateDiskSpaceHealth(string baseMessage, long? freeBytes, long? totalBytes)
    {
        if (freeBytes is not > 0)
        {
            return ("healthy", baseMessage);
        }

        var lowByAbsolute = freeBytes < LowDiskSpaceThresholdBytes;
        var lowByPercent = totalBytes is > 0 && (double)freeBytes.Value / totalBytes.Value < 0.05;
        if (!lowByAbsolute && !lowByPercent)
        {
            return ("healthy", baseMessage);
        }

        var freeGb = Math.Round(freeBytes.Value / 1_000_000_000d, 1);
        return ("degraded", $"{baseMessage} Only {freeGb.ToString(CultureInfo.InvariantCulture)} GB free — downloads will start failing once it runs out.");
    }

    /// <summary>
    /// Free space on the drive holding <paramref name="path"/>, measured at the
    /// nearest folder on that path that already exists (#1060). A download
    /// folder a client creates itself on its first download does not exist
    /// yet on a fresh install, and asking the client about it (or the drive
    /// about a folder that is not there) answers nothing, which Health showed
    /// as "could not read free space". The space a future download will use
    /// is the space of the drive it will be created on, so this walks up to the
    /// first folder that exists. Null only when the path is not a rooted path
    /// on this machine, or the drive cannot report.
    /// </summary>
    internal static long? FreeSpaceOfNearestExistingFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var current = path.Trim();
            if (!Path.IsPathRooted(current))
            {
                return null;
            }

            current = Path.GetFullPath(current);
            while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
            {
                current = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            return string.IsNullOrEmpty(current) ? null : new DriveInfo(Path.GetPathRoot(current) ?? current).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
    private const long LowDiskSpaceThresholdBytes = 5_000_000_000L;
}
