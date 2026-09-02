using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Contracts;

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
        using var response = await http.PostAsJsonAsync(uri, payload, cancellationToken);
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

        if ((progress ?? 0) >= 99.9 || normalized.Contains("complete") || normalized.Contains("seeding")) return DownloadQueueStatuses.ImportReady;
        if (normalized.Contains("pause") || normalized.Contains("queue")) return DownloadQueueStatuses.Queued;
        return DownloadQueueStatuses.Downloading;
    }
}
