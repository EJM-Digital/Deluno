using System.Net.Http.Json;
using Deluno.Infrastructure.Http;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
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
public abstract class EmbyFamilyMediaServerClient(IHttpClientFactory httpClientFactory) : IMediaServerClient, IMediaServerPlaybackReader
{
    private const int PageSize = 500;

    public abstract string Kind { get; }

    protected abstract string PathPrefix { get; }

    private string DisplayName => MediaServerKinds.DisplayName(Kind);

    public async Task<MediaServerTestResult> TestAsync(MediaServerConnection connection, CancellationToken cancellationToken)
    {
        if (connection.SecretUnreadable)
        {
            return new MediaServerTestResult(false, "degraded", connection.SecretProblem ?? Deluno.Security.SecretUnreadable.Message("API key"), null);
        }

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

        // CollapseBoxSetItems=false on every listing: Jellyfin otherwise answers
        // a film that is in a collection with the collection, even when asked
        // for films. On the rig a title in "Leaving soon" vanished from the
        // listing, so the rule read it as not on Jellyfin, heard "nobody
        // watched it" from Emby, and cleared it (#521) - the same slip would
        // let "watched by nobody" match a film somebody had watched.
        var items = new Dictionary<string, MediaServerItem>(StringComparer.OrdinalIgnoreCase);
        var wanted = itemTypes.Split(',');
        string? grouped = null;
        var catalogue = await PageAsync(
            connection,
            $"/Items?Recursive=true&IncludeItemTypes={itemTypes}&CollapseBoxSetItems=false&Fields={CatalogueFields}",
            item =>
            {
                // Should grouping happen anyway, the listing is not the library.
                if (String(item, "Type") is { } type && !wanted.Contains(type, StringComparer.OrdinalIgnoreCase))
                {
                    grouped ??= $"{connection.Name} listed \u201c{String(item, "Name")}\u201d in place of the titles in it, so its library could not be read in full.";
                    return;
                }

                items[String(item, "Id")!] = ToItem(item, mediaType);
            },
            cancellationToken);
        if (catalogue is not null || grouped is not null)
        {
            return Unreachable(catalogue ?? grouped!);
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
                    $"/Users/{Uri.EscapeDataString(user.Id!)}/Items?Recursive=true&IncludeItemTypes={itemTypes}&CollapseBoxSetItems=false&{filter}&Fields=UserData",
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
            $"/Users/{user}/Items?Recursive=true&IncludeItemTypes={containerType}&CollapseBoxSetItems=false",
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
                $"/Users/{user}/Items?ParentId={Uri.EscapeDataString(container.Id)}&CollapseBoxSetItems=false",
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

    /// <summary>What is playing now, from <c>/Sessions</c>: each play's item, season, show, file and provider ids (#1012).</summary>
    public async Task<MediaServerAnswer<IReadOnlyList<MediaServerPlay>>> ListPlayingAsync(MediaServerConnection connection, CancellationToken cancellationToken)
    {
        var call = await GetAsync(connection, "/Sessions", cancellationToken);
        using var document = call.Ok ? TryParse(call.Body) : null;
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Unreachable(call.Problem ?? $"{connection.Name} did not say what is being played.");
        }

        var plays = new List<MediaServerPlay>();
        var shows = new Dictionary<string, (string? Tmdb, string? Tvdb, string? Imdb)>(StringComparer.Ordinal);
        foreach (var session in document.RootElement.EnumerateArray())
        {
            if (!session.TryGetProperty("NowPlayingItem", out var playing) || playing.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var ids = new[] { "Id", "SeasonId", "SeriesId" }.Select(name => String(playing, name)).OfType<string>().Where(id => id.Length > 0).ToArray();
            var paths = new List<string>();
            if (String(playing, "Path") is { Length: > 0 } path) paths.Add(path);
            if (playing.TryGetProperty("MediaSources", out var sources) && sources.ValueKind == JsonValueKind.Array)
            {
                paths.AddRange(sources.EnumerateArray().Select(source => String(source, "Path")).OfType<string>().Where(file => file.Length > 0));
            }

            string? Provider(string name)
            {
                if (!playing.TryGetProperty("ProviderIds", out var providers) || providers.ValueKind != JsonValueKind.Object) return null;
                foreach (var property in providers.EnumerateObject())
                {
                    if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String) return property.Value.GetString();
                }

                return null;
            }

            var isEpisode = String(playing, "Type") == "Episode";
            (string? Tmdb, string? Tvdb, string? Imdb) show = default;
            if (isEpisode && String(playing, "SeriesId") is { Length: > 0 } seriesId && !shows.TryGetValue(seriesId, out show))
            {
                // A session lists the episode's own provider ids, not the show's: ask for the show once.
                show = await ReadShowProviderIdsAsync(connection, seriesId, cancellationToken);
                shows[seriesId] = show;
            }

            plays.Add(new MediaServerPlay(
                ids, paths.Distinct(StringComparer.Ordinal).ToArray(), String(playing, "Type") == "Movie",
                Provider("Tmdb"), Provider("Tvdb"), Provider("Imdb"),
                isEpisode, String(playing, "SeriesName"), show.Tmdb, show.Tvdb, show.Imdb,
                Int(playing, "ParentIndexNumber"), Int(playing, "IndexNumber")));
        }

        return new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported(plays);
    }

    private async Task<(string? Tmdb, string? Tvdb, string? Imdb)> ReadShowProviderIdsAsync(MediaServerConnection connection, string seriesId, CancellationToken cancellationToken)
    {
        // Not being able to ask leaves the ids null, which the caller reads as "cannot tell which show", never "not this show".
        var call = await GetAsync(connection, $"/Items?Ids={Uri.EscapeDataString(seriesId)}&Fields=ProviderIds", cancellationToken);
        using var document = call.Ok ? TryParse(call.Body) : null;
        if (document is null ||
            !document.RootElement.TryGetProperty("Items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0 ||
            !items[0].TryGetProperty("ProviderIds", out var providers) || providers.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        string? Provider(string name)
        {
            foreach (var property in providers.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String) return property.Value.GetString();
            }

            return null;
        }

        return (Provider("Tmdb"), Provider("Tvdb"), Provider("Imdb"));
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
            SizedJson.Create(new
            {
                Updates = item.Paths.Select(path => new { Path = path, UpdateType = "Deleted" }).ToArray()
            }, options: DocumentedNames));

        return result.Ok
            ? new MediaServerAnswer<MediaServerRemoval>.Reported(new MediaServerRemoval($"{connection.Name} was told {item.Title} is gone and will drop it from its library."))
            : new MediaServerAnswer<MediaServerRemoval>.Unreachable(result.Problem!);
    }

    public async Task<MediaServerAnswer<MediaServerRefresh>> RefreshAsync(
        MediaServerConnection connection,
        string mediaType,
        IReadOnlyList<MediaServerPathChange> changes,
        CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
        {
            return new MediaServerAnswer<MediaServerRefresh>.Reported(new MediaServerRefresh($"Nothing to tell {connection.Name}.", false));
        }

        var updates = changes
            .Select(change => new
            {
                Path = change.Path,
                UpdateType = change.Kind switch
                {
                    MediaServerChangeKind.Created => "Created",
                    MediaServerChangeKind.Deleted => "Deleted",
                    _ => "Modified"
                }
            })
            .ToArray();

        var result = await SendAsync(
            httpClientFactory,
            HttpMethod.Post,
            Address(connection, "/Library/Media/Updated"),
            Headers(connection),
            connection.Name,
            cancellationToken,
            SizedJson.Create(new { Updates = updates }, options: DocumentedNames));

        if (result.Ok)
        {
            return new MediaServerAnswer<MediaServerRefresh>.Reported(new MediaServerRefresh(
                $"{connection.Name} was told about {updates.Length} changed file{(updates.Length == 1 ? string.Empty : "s")}.", false));
        }

        // The targeted call failed. Rather than leave the server never told at
        // all, fall back to the full scan Radarr/Sonarr Connect also falls
        // back to when a targeted notify is not possible.
        var fallback = await SendAsync(httpClientFactory, HttpMethod.Post, Address(connection, "/Library/Refresh"), Headers(connection), connection.Name, cancellationToken);
        return fallback.Ok
            ? new MediaServerAnswer<MediaServerRefresh>.Reported(new MediaServerRefresh(
                $"{connection.Name} could not take a targeted refresh ({result.Problem}), so a full library scan was requested instead.", true))
            : new MediaServerAnswer<MediaServerRefresh>.Unreachable(result.Problem!);
    }

    public async Task<MediaServerAnswer<MediaServerPathMappingTest>> TestPathAsync(
        MediaServerConnection connection,
        string serverPath,
        CancellationToken cancellationToken)
    {
        var browse = await GetAsync(connection, $"/Environment/DirectoryContents?path={Uri.EscapeDataString(serverPath)}&includeFiles=false", cancellationToken);
        if (!browse.Ok)
        {
            return new MediaServerAnswer<MediaServerPathMappingTest>.Unreachable(browse.Problem!);
        }

        using var document = TryParse(browse.Body);
        var found = document?.RootElement.ValueKind == JsonValueKind.Array;
        return new MediaServerAnswer<MediaServerPathMappingTest>.Reported(new MediaServerPathMappingTest(
            found,
            found
                ? $"{connection.Name} can see {serverPath}."
                : $"{connection.Name} answered, but not with a folder listing for {serverPath}. Check the mapped path."));
    }

    public async Task<MediaServerAnswer<MediaServerCollectionSync>> SyncCollectionAsync(
        MediaServerConnection connection,
        MediaServerCollectionRequest request,
        CancellationToken cancellationToken)
    {
        // Found by name among collections only (BoxSet), so the id that may be
        // deleted below can only ever be a collection.
        var existing = new List<string>();
        string? overview = null;
        var listed = await PageAsync(
            connection,
            $"/Items?Recursive=true&IncludeItemTypes=BoxSet&SearchTerm={Uri.EscapeDataString(request.Name)}&Fields=Overview",
            item =>
            {
                if (string.Equals(String(item, "Name"), request.Name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(String(item, "Type"), "BoxSet", StringComparison.OrdinalIgnoreCase))
                {
                    if (existing.Count == 0) overview = String(item, "Overview");
                    existing.Add(String(item, "Id")!);
                }
            },
            cancellationToken);
        if (listed is not null)
        {
            return new MediaServerAnswer<MediaServerCollectionSync>.Unreachable(listed);
        }

        var wanted = request.Members.Select(member => member.ItemId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var collectionId = existing.FirstOrDefault();

        if (wanted.Length == 0)
        {
            foreach (var id in existing)
            {
                var deleted = await SendAsync(httpClientFactory, HttpMethod.Delete, Address(connection, $"/Items/{Uri.EscapeDataString(id)}"), Headers(connection), connection.Name, cancellationToken);
                if (!deleted.Ok)
                {
                    return new MediaServerAnswer<MediaServerCollectionSync>.Unreachable(deleted.Problem!);
                }
            }

            return new MediaServerAnswer<MediaServerCollectionSync>.Reported(new MediaServerCollectionSync(0, 0, 0, 0, existing.Count > 0));
        }

        // Read as a user: asked without one, Jellyfin answered an empty
        // collection, so a title added by hand was never taken out (#521, rig).
        // The same user id is what favourites a collection onto the home
        // screen below, so it is read once up front for both branches.
        var usersCall = await GetAsync(connection, "/Users", cancellationToken);
        using var usersDocument = usersCall.Ok ? TryParse(usersCall.Body) : null;
        var userId = usersDocument?.RootElement.ValueKind == JsonValueKind.Array
            ? usersDocument.RootElement.EnumerateArray().Select(user => String(user, "Id")).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))
            : null;
        if (userId is null)
        {
            return new MediaServerAnswer<MediaServerCollectionSync>.Unreachable(usersCall.Problem ?? $"{connection.Name} did not list its users, so its collection could not be read.");
        }

        if (collectionId is null)
        {
            var created = await SendAsync(
                httpClientFactory,
                HttpMethod.Post,
                Address(connection, $"/Collections?Name={Uri.EscapeDataString(request.Name)}&Ids={Uri.EscapeDataString(string.Join(',', wanted))}"),
                Headers(connection),
                connection.Name,
                cancellationToken);
            if (!created.Ok)
            {
                return new MediaServerAnswer<MediaServerCollectionSync>.Unreachable(created.Problem!);
            }

            using var document = TryParse(created.Body);
            collectionId = document is null ? null : String(document.RootElement, "Id");
            if (collectionId is null)
            {
                return new MediaServerAnswer<MediaServerCollectionSync>.Unreachable($"{connection.Name} did not say which collection it created.");
            }

            await DescribeAsync(connection, collectionId, request.Description, cancellationToken);
            var homed = await SetHomeAsync(connection, userId, collectionId, request.PromoteToHome, cancellationToken);
            if (homed is not null)
            {
                return new MediaServerAnswer<MediaServerCollectionSync>.Unreachable(homed);
            }

            return new MediaServerAnswer<MediaServerCollectionSync>.Reported(new MediaServerCollectionSync(wanted.Length, 0, wanted.Length, 0, false));
        }

        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var read = await PageAsync(
            connection,
            $"/Users/{Uri.EscapeDataString(userId)}/Items?ParentId={Uri.EscapeDataString(collectionId)}&CollapseBoxSetItems=false",
            item => members.Add(String(item, "Id")!),
            cancellationToken);
        if (read is not null)
        {
            return new MediaServerAnswer<MediaServerCollectionSync>.Unreachable(read);
        }

        var add = wanted.Where(id => !members.Contains(id)).ToArray();
        var remove = members.Where(id => !wanted.Contains(id, StringComparer.OrdinalIgnoreCase)).ToArray();
        foreach (var (method, ids) in new[] { (HttpMethod.Post, add), (HttpMethod.Delete, remove) })
        {
            if (ids.Length == 0)
            {
                continue;
            }

            var changed = await SendAsync(
                httpClientFactory,
                method,
                Address(connection, $"/Collections/{Uri.EscapeDataString(collectionId)}/Items?Ids={Uri.EscapeDataString(string.Join(',', ids))}"),
                Headers(connection),
                connection.Name,
                cancellationToken);
            if (!changed.Ok)
            {
                return new MediaServerAnswer<MediaServerCollectionSync>.Unreachable(changed.Problem!);
            }
        }

        // A server can take the collection before it will take its description
        // (Jellyfin on the rig), so a missing or changed one is set again.
        if (!string.Equals(overview, request.Description, StringComparison.Ordinal))
        {
            await DescribeAsync(connection, collectionId, request.Description, cancellationToken);
        }

        var homedExisting = await SetHomeAsync(connection, userId, collectionId, request.PromoteToHome, cancellationToken);
        if (homedExisting is not null)
        {
            return new MediaServerAnswer<MediaServerCollectionSync>.Unreachable(homedExisting);
        }

        return new MediaServerAnswer<MediaServerCollectionSync>.Reported(new MediaServerCollectionSync(add.Length, remove.Length, wanted.Length, 0, false));
    }

    /// <summary>
    /// Jellyfin/Emby's nearest equivalent to Plex's own-home promotion (#854):
    /// favouriting the collection puts it on the home screen's Favorites row.
    /// </summary>
    private async Task<string?> SetHomeAsync(MediaServerConnection connection, string userId, string collectionId, bool promote, CancellationToken cancellationToken)
    {
        var method = promote ? HttpMethod.Post : HttpMethod.Delete;
        var result = await SendAsync(
            httpClientFactory,
            method,
            Address(connection, $"/Users/{Uri.EscapeDataString(userId)}/FavoriteItems/{Uri.EscapeDataString(collectionId)}"),
            Headers(connection),
            connection.Name,
            cancellationToken);
        return result.Ok ? null : result.Problem;
    }

    /// <summary>
    /// Says what the collection is. Best effort: the item is read and written
    /// back whole with its overview set, and a server that will not take it
    /// still has a working collection.
    /// </summary>
    private async Task DescribeAsync(MediaServerConnection connection, string collectionId, string description, CancellationToken cancellationToken)
    {
        var read = await GetAsync(connection, $"/Items?Ids={Uri.EscapeDataString(collectionId)}&Fields=Overview,Tags,Genres,ProviderIds,People,Studios", cancellationToken);
        using var document = read.Ok ? TryParse(read.Body) : null;
        if (document is null ||
            !document.RootElement.TryGetProperty("Items", out var items) ||
            items.ValueKind != JsonValueKind.Array ||
            items.GetArrayLength() == 0)
        {
            return;
        }

        var item = System.Text.Json.Nodes.JsonNode.Parse(items[0].GetRawText())!.AsObject();
        item["Overview"] = description;
        await SendAsync(
            httpClientFactory,
            HttpMethod.Post,
            Address(connection, $"/Items/{Uri.EscapeDataString(collectionId)}"),
            Headers(connection),
            connection.Name,
            cancellationToken,
            new StringContent(item.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
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
        IReadOnlyList<string>? audioLanguages = null;
        if (item.TryGetProperty("MediaSources", out var sources) && sources.ValueKind == JsonValueKind.Array && sources.GetArrayLength() > 0)
        {
            var source = sources[0];
            audioLanguages = AudioLanguagesOfAllVersions(sources);

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
            Video = video,
            AudioLanguages = audioLanguages,
            Runtime = Long(item, "RunTimeTicks") is > 0 and var ticks ? TimeSpan.FromTicks(ticks) : null
        };
    }

    /// <summary>
    /// The audio languages across every version of the item (#1003). A version
    /// with no stream list makes the answer unknown, not "none".
    /// </summary>
    internal static IReadOnlyList<string>? AudioLanguagesOfAllVersions(JsonElement sources)
    {
        var tags = new List<string?>();
        foreach (var version in sources.EnumerateArray())
        {
            if (!version.TryGetProperty("MediaStreams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            tags.AddRange(streams.EnumerateArray().Where(entry => String(entry, "Type") == "Audio").Select(entry => String(entry, "Language")));
        }

        return MediaServerLanguages.OfAudioTracks(tags);
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
