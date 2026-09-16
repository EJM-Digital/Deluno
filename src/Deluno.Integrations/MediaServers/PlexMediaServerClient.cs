using System.Text.Json;
using Deluno.Connections.Contracts;
using static Deluno.Integrations.MediaServers.MediaServerHttp;

namespace Deluno.Integrations.MediaServers;

/// <summary>
/// Plex.
///
/// <para><b>Watched is one account's view.</b> A Plex token speaks for the
/// account it belongs to, and the library API reports that account's play
/// history only. So a Plex item's watch state says so
/// (<see cref="MediaServerWatchState.PerUserKnown"/> is false), and a rule can
/// tell "nobody watched it" apart from "this account did not".</para>
///
/// <para><b>Removal is a rescan, not a delete.</b> Plex's delete call removes
/// files when deletion is allowed. Deluno has already moved the files to its
/// recycle bin, so instead it rescans the folder they were in and empties the
/// section's trash, which is how Plex drops an item whose files are gone.</para>
/// </summary>
public sealed class PlexMediaServerClient(IHttpClientFactory httpClientFactory) : IMediaServerClient
{
    private const int PageSize = 500;

    public string Kind => MediaServerKinds.Plex;

    public async Task<MediaServerTestResult> TestAsync(MediaServerConnection connection, CancellationToken cancellationToken)
    {
        var root = await GetAsync(connection, "/", cancellationToken);
        if (!root.Ok)
        {
            return new MediaServerTestResult(false, "unreachable", root.Problem!, root.LatencyMs);
        }

        using var document = TryParse(root.Body);
        var container = Container(document);
        if (container is null || String(container.Value, "machineIdentifier") is not { Length: > 0 } serverId)
        {
            return new MediaServerTestResult(false, "degraded", $"Something answered at {connection.BaseUrl}, but not as Plex does.", root.LatencyMs);
        }

        var serverName = String(container.Value, "friendlyName");
        var sections = await GetAsync(connection, "/library/sections", cancellationToken);
        using var sectionsDocument = sections.Ok ? TryParse(sections.Body) : null;
        var libraryCount = Container(sectionsDocument) is { } list && list.TryGetProperty("Directory", out var directories) && directories.ValueKind == JsonValueKind.Array
            ? directories.GetArrayLength()
            : (int?)null;

        return new MediaServerTestResult(
            true,
            "healthy",
            $"Connected to {serverName ?? "Plex"}{(String(container.Value, "version") is { } version ? $" (Plex {version})" : string.Empty)}: {libraryCount ?? 0} libraries. Watched state is this token's account only.",
            root.LatencyMs,
            serverName,
            serverId,
            libraryCount);
    }

    public async Task<MediaServerAnswer<IReadOnlyList<MediaServerItem>>> ListItemsAsync(
        MediaServerConnection connection,
        string mediaType,
        CancellationToken cancellationToken)
    {
        var sectionType = mediaType == "tv" ? "show" : "movie";
        var sectionsCall = await GetAsync(connection, "/library/sections", cancellationToken);
        using var sectionsDocument = sectionsCall.Ok ? TryParse(sectionsCall.Body) : null;
        if (Container(sectionsDocument) is not { } sections ||
            !sections.TryGetProperty("Directory", out var directories) ||
            directories.ValueKind != JsonValueKind.Array)
        {
            return new MediaServerAnswer<IReadOnlyList<MediaServerItem>>.Unreachable(sectionsCall.Problem ?? $"{connection.Name} did not list its libraries.");
        }

        var items = new List<MediaServerItem>();
        foreach (var directory in directories.EnumerateArray())
        {
            if (!string.Equals(String(directory, "type"), sectionType, StringComparison.OrdinalIgnoreCase) ||
                String(directory, "key") is not { Length: > 0 } key)
            {
                continue;
            }

            for (var start = 0; ; start += PageSize)
            {
                var page = await GetAsync(
                    connection,
                    $"/library/sections/{Uri.EscapeDataString(key)}/all?includeGuids=1&X-Plex-Container-Start={start}&X-Plex-Container-Size={PageSize}",
                    cancellationToken);
                using var pageDocument = page.Ok ? TryParse(page.Body) : null;
                if (Container(pageDocument) is not { } container)
                {
                    return new MediaServerAnswer<IReadOnlyList<MediaServerItem>>.Unreachable(page.Problem ?? $"{connection.Name} answered with a library listing Deluno could not read.");
                }

                var count = 0;
                if (container.TryGetProperty("Metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in metadata.EnumerateArray())
                    {
                        count++;
                        if (ToItem(entry, mediaType, key) is { } item)
                        {
                            items.Add(item);
                        }
                    }
                }

                var total = Int(container, "totalSize") ?? count;
                if (count < PageSize || start + count >= total)
                {
                    break;
                }
            }
        }

        return new MediaServerAnswer<IReadOnlyList<MediaServerItem>>.Reported(items);
    }

    public async Task<MediaServerAnswer<MediaServerRemoval>> RemoveAsync(
        MediaServerConnection connection,
        MediaServerItem item,
        CancellationToken cancellationToken)
    {
        if (item.SectionId is null || item.Paths.Count == 0)
        {
            return new MediaServerAnswer<MediaServerRemoval>.NotSupported($"{connection.Name} did not say where {item.Title} is stored, so Deluno cannot rescan it away.");
        }

        // A film's paths are its files, so the folder holding them is rescanned;
        // a show's are already its folder.
        foreach (var folder in item.Paths.Select(path => item.MediaType == "tv" ? path : FolderOf(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var refresh = await GetAsync(
                connection,
                $"/library/sections/{Uri.EscapeDataString(item.SectionId)}/refresh?path={Uri.EscapeDataString(folder)}",
                cancellationToken);
            if (!refresh.Ok)
            {
                return new MediaServerAnswer<MediaServerRemoval>.Unreachable(refresh.Problem!);
            }
        }

        var emptied = await SendAsync(
            httpClientFactory,
            HttpMethod.Put,
            Address(connection, $"/library/sections/{Uri.EscapeDataString(item.SectionId)}/emptyTrash"),
            Headers(connection),
            connection.Name,
            cancellationToken);

        return emptied.Ok
            ? new MediaServerAnswer<MediaServerRemoval>.Reported(new MediaServerRemoval($"{connection.Name} rescanned where {item.Title} was and emptied its trash."))
            : new MediaServerAnswer<MediaServerRemoval>.Unreachable(emptied.Problem!);
    }

    internal static string FolderOf(string path)
    {
        var separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return separator > 0 ? path[..separator] : path;
    }

    private static MediaServerItem? ToItem(JsonElement entry, string mediaType, string sectionKey)
    {
        if (String(entry, "ratingKey") is not { Length: > 0 } ratingKey)
        {
            return null;
        }

        string? tmdb = null, imdb = null, tvdb = null;
        if (entry.TryGetProperty("Guid", out var guids) && guids.ValueKind == JsonValueKind.Array)
        {
            foreach (var guid in guids.EnumerateArray())
            {
                var id = String(guid, "id");
                if (id is null) continue;
                if (id.StartsWith("tmdb://", StringComparison.OrdinalIgnoreCase)) tmdb = id[7..];
                else if (id.StartsWith("imdb://", StringComparison.OrdinalIgnoreCase)) imdb = id[7..];
                else if (id.StartsWith("tvdb://", StringComparison.OrdinalIgnoreCase)) tvdb = id[7..];
            }
        }

        var paths = new List<string>();
        if (entry.TryGetProperty("Media", out var media) && media.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in media.EnumerateArray()
                         .Where(item => item.TryGetProperty("Part", out var parts) && parts.ValueKind == JsonValueKind.Array)
                         .SelectMany(item => item.GetProperty("Part").EnumerateArray()))
            {
                if (String(part, "file") is { Length: > 0 } file) paths.Add(file);
            }
        }

        if (entry.TryGetProperty("Location", out var locations) && locations.ValueKind == JsonValueKind.Array)
        {
            foreach (var location in locations.EnumerateArray())
            {
                if (String(location, "path") is { Length: > 0 } folder) paths.Add(folder);
            }
        }

        // A film is watched once its view count is set; a show once every
        // episode has been.
        var played = mediaType == "tv"
            ? Int(entry, "leafCount") is > 0 and var leaves && Int(entry, "viewedLeafCount") >= leaves
            : Int(entry, "viewCount") is > 0;
        var lastViewed = Long(entry, "lastViewedAt") is > 0 and var seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : (DateTimeOffset?)null;
        var added = Long(entry, "addedAt") is > 0 and var addedSeconds ? DateTimeOffset.FromUnixTimeSeconds(addedSeconds) : (DateTimeOffset?)null;

        return new MediaServerItem(
            ratingKey,
            mediaType,
            String(entry, "title") ?? "Untitled",
            Int(entry, "year"),
            tmdb,
            imdb,
            tvdb,
            paths,
            added,
            new MediaServerWatchState(played, [], lastViewed, PerUserKnown: false),
            sectionKey);
    }

    private static JsonElement? Container(JsonDocument? document)
        => document is not null && document.RootElement.TryGetProperty("MediaContainer", out var container) ? container : null;

    private Task<MediaServerCallResult> GetAsync(MediaServerConnection connection, string pathAndQuery, CancellationToken cancellationToken)
        => SendAsync(httpClientFactory, HttpMethod.Get, Address(connection, pathAndQuery), Headers(connection), connection.Name, cancellationToken);

    private static Uri Address(MediaServerConnection connection, string pathAndQuery)
        => new($"{connection.BaseUrl.TrimEnd('/')}{pathAndQuery}");

    private static IReadOnlyDictionary<string, string> Headers(MediaServerConnection connection)
        => new Dictionary<string, string>
        {
            ["X-Plex-Token"] = connection.Token ?? string.Empty,
            ["Accept"] = "application/json",
            ["X-Plex-Client-Identifier"] = "deluno",
            ["X-Plex-Product"] = "Deluno"
        };
}
