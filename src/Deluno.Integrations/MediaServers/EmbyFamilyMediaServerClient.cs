using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using static Deluno.Integrations.MediaServers.MediaServerHttp;

namespace Deluno.Integrations.MediaServers;

/// <summary>
/// Jellyfin and Emby.
///
/// <para>One implementation for two kinds because Jellyfin began as a fork of
/// Emby and the parts Deluno uses are still the same API: an API key,
/// <c>/System/Info</c>, <c>/Users</c>, <c>/Items</c> with
/// provider ids, per-user play state under <c>/Users/{id}/Items</c>, and
/// <c>/Library/Media/Updated</c> to report a deletion. Emby serves them under
/// <c>/emby</c>; Jellyfin at the root.</para>
///
/// <para><b>Watched means watched by anyone.</b> An API key sees every account,
/// so each enabled user is asked what they have finished and the answers are
/// combined: who played it, and the most recent time anyone did.</para>
/// </summary>
public abstract class EmbyFamilyMediaServerClient(IHttpClientFactory httpClientFactory) : IMediaServerClient
{
    private const int PageSize = 500;

    public abstract string Kind { get; }

    protected abstract string PathPrefix { get; }

    private string DisplayName => MediaServerKinds.DisplayName(Kind);

    public async Task<MediaServerTestResult> TestAsync(MediaServerConnection connection, CancellationToken cancellationToken)
    {
        var info = await GetAsync(connection, "/System/Info", cancellationToken);
        if (!info.Ok)
        {
            return new MediaServerTestResult(false, "unreachable", info.Problem!, info.LatencyMs);
        }

        using var document = TryParse(info.Body);
        if (document is null || String(document.RootElement, "Id") is not { Length: > 0 } serverId)
        {
            return new MediaServerTestResult(false, "degraded", $"Something answered at {connection.BaseUrl}, but not as {DisplayName} does.", info.LatencyMs);
        }

        var serverName = String(document.RootElement, "ServerName");
        var version = String(document.RootElement, "Version");
        var folders = await GetAsync(connection, "/Library/VirtualFolders", cancellationToken);
        using var foldersDocument = folders.Ok ? TryParse(folders.Body) : null;
        var users = await GetAsync(connection, "/Users", cancellationToken);
        using var usersDocument = users.Ok ? TryParse(users.Body) : null;

        int? libraryCount = foldersDocument?.RootElement.ValueKind == JsonValueKind.Array ? foldersDocument.RootElement.GetArrayLength() : null;
        int? userCount = usersDocument?.RootElement.ValueKind == JsonValueKind.Array ? usersDocument.RootElement.GetArrayLength() : null;
        if (userCount is null)
        {
            return new MediaServerTestResult(
                false,
                "degraded",
                $"Connected to {serverName ?? DisplayName}, but the API key cannot list users, so Deluno could not tell what anyone has watched. Use an administrator's API key.",
                info.LatencyMs,
                serverName,
                serverId);
        }

        return new MediaServerTestResult(
            true,
            "healthy",
            $"Connected to {serverName ?? DisplayName}{(version is null ? string.Empty : $" ({DisplayName} {version})")}: {libraryCount ?? 0} libraries, {userCount} users.",
            info.LatencyMs,
            serverName,
            serverId,
            libraryCount,
            userCount);
    }

    public async Task<MediaServerAnswer<IReadOnlyList<MediaServerItem>>> ListItemsAsync(
        MediaServerConnection connection,
        string mediaType,
        CancellationToken cancellationToken)
    {
        var itemType = mediaType == "tv" ? "Series" : "Movie";

        var usersCall = await GetAsync(connection, "/Users", cancellationToken);
        using var usersDocument = usersCall.Ok ? TryParse(usersCall.Body) : null;
        if (usersDocument?.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new MediaServerAnswer<IReadOnlyList<MediaServerItem>>.Unreachable(
                usersCall.Problem ?? $"{connection.Name} did not list its users, so what has been watched cannot be known.");
        }

        var users = usersDocument.RootElement.EnumerateArray()
            .Where(user => !(user.TryGetProperty("Policy", out var policy) && Bool(policy, "IsDisabled")))
            .Select(user => (Id: String(user, "Id"), Name: String(user, "Name") ?? "Unnamed user"))
            .Where(user => !string.IsNullOrWhiteSpace(user.Id))
            .ToArray();

        var items = new Dictionary<string, MediaServerItem>(StringComparer.OrdinalIgnoreCase);
        var catalogue = await PageAsync(
            connection,
            $"/Items?Recursive=true&IncludeItemTypes={itemType}&Fields=ProviderIds,Path,DateCreated,ProductionYear",
            item => items[String(item, "Id")!] = ToItem(item, mediaType),
            cancellationToken);
        if (catalogue is not null)
        {
            return new MediaServerAnswer<IReadOnlyList<MediaServerItem>>.Unreachable(catalogue);
        }

        var playedBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var lastPlayed = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in users)
        {
            // A film is played when its own play state says so. A show is
            // played when every episode is - which Series.UserData.Played
            // already means - and was last played when its latest episode was.
            var played = await PageAsync(
                connection,
                $"/Users/{Uri.EscapeDataString(user.Id!)}/Items?Recursive=true&IncludeItemTypes={itemType}&IsPlayed=true&Fields=UserData",
                item =>
                {
                    var id = String(item, "Id")!;
                    if (!playedBy.TryGetValue(id, out var names))
                    {
                        playedBy[id] = names = [];
                    }

                    names.Add(user.Name);
                    Remember(lastPlayed, id, item.TryGetProperty("UserData", out var data) ? Date(data, "LastPlayedDate") : null);
                },
                cancellationToken);
            if (played is not null)
            {
                return new MediaServerAnswer<IReadOnlyList<MediaServerItem>>.Unreachable(played);
            }

            if (mediaType == "tv")
            {
                var episodes = await PageAsync(
                    connection,
                    $"/Users/{Uri.EscapeDataString(user.Id!)}/Items?Recursive=true&IncludeItemTypes=Episode&IsPlayed=true&Fields=UserData,SeriesId",
                    episode =>
                    {
                        if (String(episode, "SeriesId") is { Length: > 0 } seriesId)
                        {
                            Remember(lastPlayed, seriesId, episode.TryGetProperty("UserData", out var data) ? Date(data, "LastPlayedDate") : null);
                        }
                    },
                    cancellationToken);
                if (episodes is not null)
                {
                    return new MediaServerAnswer<IReadOnlyList<MediaServerItem>>.Unreachable(episodes);
                }
            }
        }

        return new MediaServerAnswer<IReadOnlyList<MediaServerItem>>.Reported(
            items.Values
                .Select(item => item with
                {
                    Watch = new MediaServerWatchState(
                        playedBy.ContainsKey(item.ServerItemId),
                        playedBy.TryGetValue(item.ServerItemId, out var names) ? names : [],
                        lastPlayed.TryGetValue(item.ServerItemId, out var when) ? when : null,
                        PerUserKnown: true)
                })
                .ToArray());
    }

    public async Task<MediaServerAnswer<MediaServerRemoval>> RemoveAsync(
        MediaServerConnection connection,
        MediaServerItem item,
        CancellationToken cancellationToken)
    {
        if (item.Paths.Count == 0)
        {
            return new MediaServerAnswer<MediaServerRemoval>.NotSupported($"{connection.Name} did not say where {item.Title} is stored, so Deluno cannot report it deleted.");
        }

        var result = await SendAsync(
            httpClientFactory,
            HttpMethod.Post,
            Address(connection, "/Library/Media/Updated"),
            Headers(connection),
            connection.Name,
            cancellationToken,
            JsonContent.Create(new
            {
                Updates = item.Paths.Select(path => new { Path = path, UpdateType = "Deleted" }).ToArray()
            }, options: DocumentedNames));

        return result.Ok
            ? new MediaServerAnswer<MediaServerRemoval>.Reported(new MediaServerRemoval($"{connection.Name} was told {item.Title} is gone and will drop it from its library."))
            : new MediaServerAnswer<MediaServerRemoval>.Unreachable(result.Problem!);
    }

    // The names as the servers document them. Jellyfin reads either case;
    // Emby's older request binding is not promised to.
    private static readonly JsonSerializerOptions DocumentedNames = new() { PropertyNamingPolicy = null };

    private static void Remember(IDictionary<string, DateTimeOffset> latest, string id, DateTimeOffset? when)
    {
        if (when is { } value && (!latest.TryGetValue(id, out var current) || value > current))
        {
            latest[id] = value;
        }
    }

    /// <returns>Null when every page was read; otherwise why it could not be.</returns>
    private async Task<string?> PageAsync(
        MediaServerConnection connection,
        string query,
        Action<JsonElement> each,
        CancellationToken cancellationToken)
    {
        for (var start = 0; ; start += PageSize)
        {
            var page = await GetAsync(connection, $"{query}&StartIndex={start}&Limit={PageSize}", cancellationToken);
            if (!page.Ok)
            {
                return page.Problem;
            }

            using var document = TryParse(page.Body);
            if (document is null ||
                !document.RootElement.TryGetProperty("Items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return $"{connection.Name} answered with a library listing Deluno could not read.";
            }

            var count = 0;
            foreach (var item in items.EnumerateArray())
            {
                count++;
                if (!string.IsNullOrWhiteSpace(String(item, "Id")))
                {
                    each(item);
                }
            }

            var total = Int(document.RootElement, "TotalRecordCount") ?? count;
            if (count < PageSize || start + count >= total)
            {
                return null;
            }
        }
    }

    private static MediaServerItem ToItem(JsonElement item, string mediaType)
    {
        var providers = item.TryGetProperty("ProviderIds", out var ids) && ids.ValueKind == JsonValueKind.Object ? ids : default;
        return new MediaServerItem(
            String(item, "Id")!,
            mediaType,
            String(item, "Name") ?? "Untitled",
            Int(item, "ProductionYear"),
            ProviderId(providers, "Tmdb"),
            ProviderId(providers, "Imdb"),
            ProviderId(providers, "Tvdb"),
            String(item, "Path") is { Length: > 0 } path ? [path] : [],
            Date(item, "DateCreated"),
            MediaServerWatchState.Unplayed);
    }

    private static string? ProviderId(JsonElement providers, string name)
    {
        if (providers.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // The case of the key varies between versions: "Tmdb", "tmdb".
        foreach (var property in providers.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String &&
                property.Value.GetString() is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    private Task<MediaServerCallResult> GetAsync(MediaServerConnection connection, string pathAndQuery, CancellationToken cancellationToken)
        => SendAsync(httpClientFactory, HttpMethod.Get, Address(connection, pathAndQuery), Headers(connection), connection.Name, cancellationToken);

    private Uri Address(MediaServerConnection connection, string pathAndQuery)
    {
        var baseUrl = connection.BaseUrl.TrimEnd('/');
        var prefix = PathPrefix.Length > 0 && baseUrl.EndsWith(PathPrefix, StringComparison.OrdinalIgnoreCase) ? string.Empty : PathPrefix;
        return new Uri($"{baseUrl}{prefix}{pathAndQuery}");
    }

    private IReadOnlyDictionary<string, string> Headers(MediaServerConnection connection)
    {
        var headers = new Dictionary<string, string> { ["Accept"] = "application/json" };
        headers[TokenHeaderName] = TokenHeaderValue(connection.Token ?? string.Empty);
        return headers;
    }

    protected abstract string TokenHeaderName { get; }

    protected abstract string TokenHeaderValue(string token);
}

public sealed class JellyfinMediaServerClient(IHttpClientFactory httpClientFactory) : EmbyFamilyMediaServerClient(httpClientFactory)
{
    public override string Kind => MediaServerKinds.Jellyfin;

    protected override string PathPrefix => string.Empty;

    // Jellyfin 10.11 turned the legacy X-Emby-Token header off by default, and
    // 12.1 on the rig refused it with 401 while accepting this form.
    protected override string TokenHeaderName => "Authorization";

    protected override string TokenHeaderValue(string token) => $"MediaBrowser Token=\"{token}\"";
}

public sealed class EmbyMediaServerClient(IHttpClientFactory httpClientFactory) : EmbyFamilyMediaServerClient(httpClientFactory)
{
    public override string Kind => MediaServerKinds.Emby;

    protected override string PathPrefix => "/emby";

    protected override string TokenHeaderName => "X-Emby-Token";

    protected override string TokenHeaderValue(string token) => token;
}
