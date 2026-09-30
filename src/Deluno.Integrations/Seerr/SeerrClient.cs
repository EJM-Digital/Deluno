using System.Globalization;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Integrations.MediaServers;
using static Deluno.Integrations.MediaServers.MediaServerHttp;

namespace Deluno.Integrations.Seerr;

/// <summary>One request, as Seerr keeps it.</summary>
/// <param name="MediaId">Seerr's own media record, the one cleared when the title goes.</param>
/// <param name="ApprovedUtc">When the request was approved; null while it waits or after it was declined.</param>
public sealed record SeerrRequest(
    int MediaId,
    string MediaType,
    string? TmdbId,
    string? TvdbId,
    string RequestedBy,
    DateTimeOffset RequestedUtc,
    DateTimeOffset? ApprovedUtc);

/// <param name="Users">Everyone Seerr knows, so "everyone requested it" can be asked.</param>
public sealed record SeerrLibrary(IReadOnlyList<SeerrRequest> Requests, IReadOnlyList<string> Users);

/// <summary>What clearing a title from Seerr did.</summary>
/// <param name="Cleared">False when Seerr had no record of the title: there was nothing to clear.</param>
public sealed record SeerrClearing(bool Cleared, string Message);

public interface ISeerrClient
{
    Task<SeerrTestResult> TestAsync(SeerrConnection connection, CancellationToken cancellationToken);

    /// <summary>Every request. A listing that could not be read completely is unreachable, never "nobody requested it".</summary>
    Task<MediaServerAnswer<SeerrLibrary>> ListRequestsAsync(SeerrConnection connection, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes Seerr's media record for a title, which clears its requests, so
    /// Seerr shows it as not available rather than still on the server.
    /// </summary>
    /// <param name="mediaType"><c>movies</c> or <c>tv</c>.</param>
    Task<MediaServerAnswer<SeerrClearing>> ClearAsync(SeerrConnection connection, string mediaType, string? tmdbId, string? tvdbId, CancellationToken cancellationToken);

    /// <summary>
    /// Reports a title available (#852) the same way Overseerr's own scanners
    /// do, by calling its internal <c>POST /api/v1/media/{id}/available</c>.
    /// </summary>
    /// <param name="seerrMediaId">Seerr's own media record id, not a TMDb/TVDb id.</param>
    Task<MediaServerAnswer<bool>> MarkAvailableAsync(SeerrConnection connection, int seerrMediaId, CancellationToken cancellationToken);
}

/// <summary>
/// Jellyseerr and Overseerr, which share the API Deluno uses: <c>/api/v1/status</c>,
/// <c>/api/v1/settings/main</c>, <c>/api/v1/request</c>, <c>/api/v1/user</c>, the
/// movie and TV lookups by TMDb id, and <c>DELETE /api/v1/media/{id}</c>, all with
/// the <c>X-Api-Key</c> header (#522).
/// </summary>
public sealed class SeerrClient(IHttpClientFactory httpClientFactory) : ISeerrClient
{
    private const int PageSize = 100;

    /// <summary>Seerr's request statuses that mean it was approved.</summary>
    private static readonly int[] Approved = [2, 5];

    public async Task<SeerrTestResult> TestAsync(SeerrConnection connection, CancellationToken cancellationToken)
    {
        if (connection.SecretUnreadable)
        {
            return new SeerrTestResult(false, "degraded", connection.SecretProblem ?? Deluno.Security.SecretUnreadable.Message("API key"), null);
        }

        var status = await GetAsync(connection, "/api/v1/status", cancellationToken);
        if (!status.Ok)
        {
            return new SeerrTestResult(false, "unreachable", status.Problem!, status.LatencyMs);
        }

        using var statusDocument = TryParse(status.Body);
        if (statusDocument is null || String(statusDocument.RootElement, "version") is not { Length: > 0 } version)
        {
            return new SeerrTestResult(false, "degraded", $"Something answered at {connection.BaseUrl}, but not as Seerr does.", status.LatencyMs);
        }

        // The status page is public; settings need the key, so this is what proves it.
        var settings = await GetAsync(connection, "/api/v1/settings/main", cancellationToken);
        if (!settings.Ok)
        {
            return new SeerrTestResult(false, "degraded", settings.Problem!, status.LatencyMs);
        }

        using var settingsDocument = TryParse(settings.Body);
        var title = settingsDocument is null ? null : String(settingsDocument.RootElement, "applicationTitle");
        return new SeerrTestResult(true, "healthy", $"Connected to {title ?? "Seerr"} (version {version}).", status.LatencyMs, title, version);
    }

    public async Task<MediaServerAnswer<SeerrLibrary>> ListRequestsAsync(SeerrConnection connection, CancellationToken cancellationToken)
    {
        var requests = new List<SeerrRequest>();
        var problem = await PageAsync(connection, "/api/v1/request?filter=all&sort=added", entry =>
        {
            if (!entry.TryGetProperty("media", out var media) || Int(media, "id") is not { } mediaId)
            {
                return;
            }

            var created = Date(entry, "createdAt");
            if (created is null)
            {
                return;
            }

            var status = Int(entry, "status") ?? 0;
            requests.Add(new SeerrRequest(
                mediaId,
                String(media, "mediaType") == "tv" ? "tv" : "movies",
                Id(media, "tmdbId"),
                Id(media, "tvdbId"),
                entry.TryGetProperty("requestedBy", out var who) ? Person(who) : "Unknown user",
                created.Value,
                Approved.Contains(status) ? Date(entry, "updatedAt") ?? created : null));
        }, cancellationToken);
        if (problem is not null)
        {
            return new MediaServerAnswer<SeerrLibrary>.Unreachable(problem);
        }

        var users = new List<string>();
        var usersProblem = await PageAsync(connection, "/api/v1/user?sort=displayname", entry => users.Add(Person(entry)), cancellationToken);
        if (usersProblem is not null)
        {
            return new MediaServerAnswer<SeerrLibrary>.Unreachable(usersProblem);
        }

        return new MediaServerAnswer<SeerrLibrary>.Reported(new SeerrLibrary(requests, users.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
    }

    public async Task<MediaServerAnswer<SeerrClearing>> ClearAsync(SeerrConnection connection, string mediaType, string? tmdbId, string? tvdbId, CancellationToken cancellationToken)
    {
        int? mediaId = null;
        if (!string.IsNullOrWhiteSpace(tmdbId))
        {
            var lookup = await GetAsync(connection, $"/api/v1/{(mediaType == "tv" ? "tv" : "movie")}/{Uri.EscapeDataString(tmdbId)}", cancellationToken);
            if (!lookup.Ok && lookup.StatusCode != 404)
            {
                return new MediaServerAnswer<SeerrClearing>.Unreachable(lookup.Problem!);
            }

            using var document = lookup.Ok ? TryParse(lookup.Body) : null;
            if (document is not null && document.RootElement.TryGetProperty("mediaInfo", out var info) && info.ValueKind == JsonValueKind.Object)
            {
                mediaId = Int(info, "id");
            }
        }
        else if (!string.IsNullOrWhiteSpace(tvdbId))
        {
            // A show Deluno knows only by TVDb id: find its record among Seerr's.
            var problem = await PageAsync(connection, "/api/v1/media?filter=all&sort=added", entry =>
            {
                if (mediaId is null && string.Equals(Id(entry, "tvdbId"), tvdbId, StringComparison.Ordinal) && String(entry, "mediaType") == "tv")
                {
                    mediaId = Int(entry, "id");
                }
            }, cancellationToken);
            if (problem is not null)
            {
                return new MediaServerAnswer<SeerrClearing>.Unreachable(problem);
            }
        }
        else
        {
            return new MediaServerAnswer<SeerrClearing>.NotSupported($"{connection.Name} knows titles by TMDb or TVDb id, and Deluno has neither for this one.");
        }

        if (mediaId is null)
        {
            return new MediaServerAnswer<SeerrClearing>.Reported(new SeerrClearing(false, $"{connection.Name} had no record of it."));
        }

        var deleted = await SendAsync(httpClientFactory, HttpMethod.Delete, Address(connection, $"/api/v1/media/{mediaId.Value.ToString(CultureInfo.InvariantCulture)}"), Headers(connection), connection.Name, cancellationToken);
        return deleted.Ok || deleted.StatusCode == 404
            ? new MediaServerAnswer<SeerrClearing>.Reported(new SeerrClearing(true, $"{connection.Name} no longer lists it, and its requests are cleared."))
            : new MediaServerAnswer<SeerrClearing>.Unreachable(deleted.Problem!);
    }

    public async Task<MediaServerAnswer<bool>> MarkAvailableAsync(SeerrConnection connection, int seerrMediaId, CancellationToken cancellationToken)
    {
        var result = await SendAsync(
            httpClientFactory,
            HttpMethod.Post,
            Address(connection, $"/api/v1/media/{seerrMediaId.ToString(CultureInfo.InvariantCulture)}/available"),
            Headers(connection),
            connection.Name,
            cancellationToken);
        return result.Ok
            ? new MediaServerAnswer<bool>.Reported(true)
            : new MediaServerAnswer<bool>.Unreachable(result.Problem!);
    }

    /// <summary>Who someone is, the way Seerr shows them.</summary>
    private static string Person(JsonElement user)
        => String(user, "displayName") ?? String(user, "jellyfinUsername") ?? String(user, "plexUsername") ?? String(user, "username") ?? String(user, "email") ?? "Unknown user";

    private static string? Id(JsonElement element, string name)
        => Int(element, name)?.ToString(CultureInfo.InvariantCulture) ?? String(element, name);

    private static DateTimeOffset? Date(JsonElement element, string name)
        => String(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when : null;

    /// <returns>Null when every page was read; otherwise why not.</returns>
    private async Task<string?> PageAsync(SeerrConnection connection, string query, Action<JsonElement> each, CancellationToken cancellationToken)
    {
        for (var skip = 0; ; skip += PageSize)
        {
            var page = await GetAsync(connection, $"{query}&take={PageSize}&skip={skip}", cancellationToken);
            if (!page.Ok)
            {
                return page.Problem;
            }

            using var document = TryParse(page.Body);
            if (document is null || !document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                return $"{connection.Name} answered with a list Deluno could not read.";
            }

            var count = 0;
            foreach (var entry in results.EnumerateArray())
            {
                count++;
                each(entry);
            }

            var total = document.RootElement.TryGetProperty("pageInfo", out var info) ? Int(info, "results") : null;
            if (count < PageSize || (total is not null && skip + count >= total))
            {
                return null;
            }
        }
    }

    private Task<MediaServerCallResult> GetAsync(SeerrConnection connection, string pathAndQuery, CancellationToken cancellationToken)
        => SendAsync(httpClientFactory, HttpMethod.Get, Address(connection, pathAndQuery), Headers(connection), connection.Name, cancellationToken);

    private static Uri Address(SeerrConnection connection, string pathAndQuery)
        => new($"{connection.BaseUrl.TrimEnd('/')}{pathAndQuery}");

    private static IReadOnlyDictionary<string, string> Headers(SeerrConnection connection)
        => new Dictionary<string, string>
        {
            ["X-Api-Key"] = connection.ApiKey ?? string.Empty,
            ["Accept"] = "application/json"
        };
}
