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

    public async Task<MediaServerAnswer<MediaServerLibrary>> ListItemsAsync(
        MediaServerConnection connection,
        string mediaType,
        CancellationToken cancellationToken)
    {
        var itemTypes = mediaType == "tv" ? "Series,Season,Episode" : "Movie";

        var usersCall = await GetAsync(connection, "/Users", cancellationToken);
        using var usersDocument = usersCall.Ok ? TryParse(usersCall.Body) : null;
        if (usersDocument?.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Unreachable(usersCall.Problem ?? $"{connection.Name} did not list its users, so what has been watched cannot be known.");
        }

        var users = usersDocument.RootElement.EnumerateArray()
            .Where(user => !(user.TryGetProperty("Policy", out var policy) && Bool(policy, "IsDisabled")))
            .Select(user => (Id: String(user, "Id"), Name: String(user, "Name") ?? "Unnamed user"))
            .Where(user => !string.IsNullOrWhiteSpace(user.Id))
            .ToArray();

        var items = new Dictionary<string, MediaServerItem>(StringComparer.OrdinalIgnoreCase);
        var catalogue = await PageAsync(
            connection,
            $"/Items?Recursive=true&IncludeItemTypes={itemTypes}&Fields={CatalogueFields}",
            item => items[String(item, "Id")!] = ToItem(item, mediaType),
            cancellationToken);
        if (catalogue is not null)
        {
            return Unreachable(catalogue);
        }

        // What each person has done: finished, favourited, or started and left.
        // Three narrow questions rather than every item for every user.
        var viewers = new Dictionary<string, Dictionary<string, MediaServerViewer>>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in users)
        {
            foreach (var filter in new[] { "IsPlayed=true", "IsFavorite=true", "Filters=IsResumable" })
            {
                var answered = await PageAsync(
                    connection,
                    $"/Users/{Uri.EscapeDataString(user.Id!)}/Items?Recursive=true&IncludeItemTypes={itemTypes}&{filter}&Fields=UserData",
                    item =>
                    {
                        if (!item.TryGetProperty("UserData", out var data))
                        {
                            return;
                        }

                        var id = String(item, "Id")!;
                        if (!viewers.TryGetValue(id, out var byUser))
                        {
                            viewers[id] = byUser = new Dictionary<string, MediaServerViewer>(StringComparer.OrdinalIgnoreCase);
                        }

                        var played = Bool(data, "Played");
                        byUser[user.Name] = new MediaServerViewer(
                            user.Name,
                            Played: played,
                            PlayCount: Int(data, "PlayCount") ?? (played ? 1 : 0),
                            LastPlayedUtc: Date(data, "LastPlayedDate"),
                            InProgress: !played && (Long(data, "PlaybackPositionTicks") ?? 0) > 0,
                            Favourite: Bool(data, "IsFavorite"),
                            Rating: Double(data, "Rating"));
                    },
                    cancellationToken);
                if (answered is not null)
                {
                    return Unreachable(answered);
                }
            }
        }

        var collections = await MembershipAsync(connection, users.Select(user => user.Id).FirstOrDefault(), "BoxSet", cancellationToken);
        if (collections.Problem is not null)
        {
            return Unreachable(collections.Problem);
        }

        var playlists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in users)
        {
            var owned = await MembershipAsync(connection, user.Id, "Playlist", cancellationToken);
            if (owned.Problem is not null)
            {
                return Unreachable(owned.Problem);
            }

            foreach (var (id, names) in owned.Names)
            {
                if (!playlists.TryGetValue(id, out var list)) playlists[id] = list = [];
                list.AddRange(names.Where(name => !list.Contains(name, StringComparer.OrdinalIgnoreCase)));
            }
        }

        return new MediaServerAnswer<MediaServerLibrary>.Reported(new MediaServerLibrary(
            items.Values
                .Select(item =>
                {
                    var seen = viewers.TryGetValue(item.ServerItemId, out var byUser) ? byUser.Values.ToArray() : [];
                    var played = seen.Where(viewer => viewer.Played).ToArray();
                    return item with
                    {
                        Viewers = seen,
                        Collections = collections.Names.TryGetValue(item.ServerItemId, out var inCollections) ? inCollections : [],
                        Playlists = playlists.TryGetValue(item.ServerItemId, out var inPlaylists) ? inPlaylists : [],
                        Watch = new MediaServerWatchState(
                            played.Length > 0,
                            played.Select(viewer => viewer.User).ToArray(),
                            seen.Select(viewer => viewer.LastPlayedUtc).Where(when => when is not null).Max(),
                            PerUserKnown: true)
                    };
                })
                .ToArray(),
            users.Select(user => user.Name).ToArray()));
    }

    private const string CatalogueFields =
        "ProviderIds,Path,DateCreated,ProductionYear,PremiereDate,Genres,People,Tags,TagItems,CommunityRating,CriticRating,MediaSources,SeriesId,SeasonId,ParentIndexNumber,IndexNumber";

    private static MediaServerAnswer<MediaServerLibrary> Unreachable(string reason)
        => new MediaServerAnswer<MediaServerLibrary>.Unreachable(reason);

    /// <summary>
    /// Which items sit in which collections (<c>BoxSet</c>) or playlists, by
    /// name. Asked as a user because both are browsed as one.
    /// </summary>
    private async Task<(Dictionary<string, List<string>> Names, string? Problem)> MembershipAsync(
        MediaServerConnection connection,
        string? userId,
        string containerType,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return (names, null);
        }

        var user = Uri.EscapeDataString(userId);
        var containers = new List<(string Id, string Name)>();
        var listed = await PageAsync(
            connection,
            $"/Users/{user}/Items?Recursive=true&IncludeItemTypes={containerType}",
            item => containers.Add((String(item, "Id")!, String(item, "Name") ?? containerType)),
            cancellationToken);
        if (listed is not null)
        {
            return (names, listed);
        }

        foreach (var container in containers)
        {
            var members = await PageAsync(
                connection,
                $"/Users/{user}/Items?ParentId={Uri.EscapeDataString(container.Id)}",
                item =>
                {
                    var id = String(item, "Id")!;
                    if (!names.TryGetValue(id, out var list)) names[id] = list = [];
                    if (!list.Contains(container.Name, StringComparer.OrdinalIgnoreCase)) list.Add(container.Name);
                },
                cancellationToken);
            if (members is not null)
            {
                return (names, members);
            }
        }

        return (names, null);
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
        var level = String(item, "Type") switch
        {
            "Series" => MediaServerItemLevels.Show,
            "Season" => MediaServerItemLevels.Season,
            "Episode" => MediaServerItemLevels.Episode,
            _ => mediaType == "tv" ? MediaServerItemLevels.Show : MediaServerItemLevels.Movie
        };

        // The first source is the file the server plays; its video stream is
        // the picture a person sees.
        var video = MediaServerVideo.None;
        if (item.TryGetProperty("MediaSources", out var sources) && sources.ValueKind == JsonValueKind.Array && sources.GetArrayLength() > 0)
        {
            var source = sources[0];
            var stream = source.TryGetProperty("MediaStreams", out var streams) && streams.ValueKind == JsonValueKind.Array
                ? streams.EnumerateArray().FirstOrDefault(entry => String(entry, "Type") == "Video")
                : default;
            var bitrate = Long(source, "Bitrate");
            video = new MediaServerVideo(
                stream.ValueKind == JsonValueKind.Object ? ResolutionLabel(Int(stream, "Width"), Int(stream, "Height")) : null,
                bitrate is > 0 ? bitrate / 1000 : null,
                stream.ValueKind == JsonValueKind.Object ? String(stream, "Codec") : null);
        }

        // CriticRating is Rotten Tomatoes' critic score out of a hundred;
        // CommunityRating is the audience score the metadata provider (TMDb) gave.
        var critic = Double(item, "CriticRating") is { } criticScore ? Math.Round(criticScore / 10d, 1) : (double?)null;
        var community = Double(item, "CommunityRating");
        var tags = Names(item, "Tags");
        if (tags.Count == 0) tags = Names(item, "TagItems");

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
            MediaServerWatchState.Unplayed)
        {
            Level = level,
            ShowServerItemId = level is MediaServerItemLevels.Season or MediaServerItemLevels.Episode ? String(item, "SeriesId") : null,
            SeasonServerItemId = level == MediaServerItemLevels.Episode ? String(item, "SeasonId") : null,
            SeasonNumber = level == MediaServerItemLevels.Season ? Int(item, "IndexNumber") : level == MediaServerItemLevels.Episode ? Int(item, "ParentIndexNumber") : null,
            EpisodeNumber = level == MediaServerItemLevels.Episode ? Int(item, "IndexNumber") : null,
            ReleaseDate = Date(item, "PremiereDate"),
            Genres = Names(item, "Genres"),
            People = Names(item, "People"),
            Labels = tags,
            Ratings = new MediaServerRatings(Critic: critic, Audience: community, RottenTomatoesCritic: critic, Tmdb: community),
            Video = video
        };
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
