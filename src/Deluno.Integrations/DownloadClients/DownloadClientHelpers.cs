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
    internal static Uri? ResolveEndpoint(DownloadClientItem client)
    {
        if (!string.IsNullOrWhiteSpace(client.EndpointUrl) &&
            Uri.TryCreate(EnsureTrailingSlash(client.EndpointUrl), UriKind.Absolute, out var endpoint))
        {
            return endpoint;
        }

        if (string.IsNullOrWhiteSpace(client.Host)) return null;
        var scheme = client.Host.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? string.Empty : "http://";
        var port = client.Port is > 0 ? $":{client.Port}" : string.Empty;
        return Uri.TryCreate(EnsureTrailingSlash($"{scheme}{client.Host}{port}"), UriKind.Absolute, out var uri) ? uri : null;
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
            : item.ErrorMessage.Trim();
        return item with
        {
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
    /// leaves it null). It defaults to <see cref="DownloadHealthStrikes.Window"/>
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
            capturedUtc - item.AddedUtc >= DownloadHealthStrikes.Window)
        {
            return item with { Status = DownloadQueueStatuses.Stalled };
        }

        if (item.Status == DownloadQueueStatuses.Downloading &&
            lastProgressUtc is { } since &&
            capturedUtc - since >= (noProgressWindow ?? DownloadHealthStrikes.Window))
        {
            return item with { Status = DownloadQueueStatuses.Stalled };
        }

        return item;
    }

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
        if (normalized.Contains("error") || normalized.Contains("fail") || normalized.Contains("stalled"))
        {
            return DownloadQueueStatuses.Stalled;
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

        if ((progress ?? 0) >= 99.9 || normalized.Contains("complete") || normalized.Contains("seeding")) return DownloadQueueStatuses.ImportReady;
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

    private const long LowDiskSpaceThresholdBytes = 5_000_000_000L;
}
