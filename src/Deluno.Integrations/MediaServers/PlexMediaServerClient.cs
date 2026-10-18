using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
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
public sealed class PlexMediaServerClient(IHttpClientFactory httpClientFactory) : IMediaServerClient, IMediaServerAudioLanguageReader, IMediaServerPlaybackReader
{
    private const int PageSize = 500;

    public string Kind => MediaServerKinds.Plex;

    public async Task<MediaServerTestResult> TestAsync(MediaServerConnection connection, CancellationToken cancellationToken)
    {
        if (connection.SecretUnreadable)
        {
            return new MediaServerTestResult(false, "degraded", connection.SecretProblem ?? Deluno.Security.SecretUnreadable.Message("access token"), null);
        }

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
            libraryCount,
            Version: Deluno.Connections.Versions.ConnectionVersionParsers.Clean(String(container.Value, "version")));
    }

    public async Task<MediaServerAnswer<MediaServerLibrary>> ListItemsAsync(
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
            return Unreachable(sectionsCall.Problem ?? $"{connection.Name} did not list its libraries.");
        }

        // A Plex token speaks for one account; that account is who "watched" means.
        var accountCall = await GetAsync(connection, "/myplex/account", cancellationToken);
        using var accountDocument = accountCall.Ok ? TryParse(accountCall.Body) : null;
        var account = accountDocument is not null && accountDocument.RootElement.TryGetProperty("MyPlex", out var myPlex)
            ? String(myPlex, "username")
            : null;
        account = string.IsNullOrWhiteSpace(account) ? "Plex account" : account;
        // Only when the server says this is the one and only account on it is "nobody watched it" a claim about everyone (#1074).
        var accountIsEveryone = await AccountIsTheOnlyOneAsync(connection, cancellationToken);

        // Movies are type 1; shows, seasons and episodes 2, 3 and 4.
        var types = mediaType == "tv" ? new[] { 2, 3, 4 } : [1];
        var items = new List<MediaServerItem>();
        string? grouped = null;
        foreach (var directory in directories.EnumerateArray())
        {
            if (!string.Equals(String(directory, "type"), sectionType, StringComparison.OrdinalIgnoreCase) ||
                String(directory, "key") is not { Length: > 0 } key)
            {
                continue;
            }

            foreach (var type in types)
            {
                var problem = await PageAsync(
                    connection,
                    $"/library/sections/{Uri.EscapeDataString(key)}/all?type={type}&includeGuids=1",
                    entry =>
                    {
                        // A section set to hide what is in collections lists the
                        // collection instead; that listing is not the library (#521).
                        if (string.Equals(String(entry, "type"), "collection", StringComparison.OrdinalIgnoreCase))
                        {
                            grouped ??= $"{connection.Name} listed the collection \u201c{String(entry, "title")}\u201d in place of the titles in it, so its library could not be read in full. Turn off hiding items in collections for that library.";
                            return;
                        }

                        if (ToItem(entry, mediaType, key, account, accountIsEveryone) is { } item)
                        {
                            items.Add(item);
                        }
                    },
                    cancellationToken);
                if (problem is not null || grouped is not null)
                {
                    return Unreachable(problem ?? grouped!);
                }
            }
        }

        // A listing carries only the first few people and tags. A film's or
        // show's own page carries all of them, and the scores from IMDb, Rotten
        // Tomatoes and TMDb.
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (item.Level is not (MediaServerItemLevels.Movie or MediaServerItemLevels.Show))
            {
                continue;
            }

            var detail = await GetAsync(connection, $"/library/metadata/{Uri.EscapeDataString(item.ServerItemId)}", cancellationToken);
            using var detailDocument = detail.Ok ? TryParse(detail.Body) : null;
            if (Container(detailDocument) is not { } container ||
                !container.TryGetProperty("Metadata", out var metadata) ||
                metadata.ValueKind != JsonValueKind.Array ||
                metadata.GetArrayLength() == 0)
            {
                return Unreachable(detail.Problem ?? $"{connection.Name} did not describe {item.Title}.");
            }

            items[index] = WithDetail(item, metadata[0]);
        }

        var playlists = await PlaylistsAsync(connection, cancellationToken);
        if (playlists.Problem is not null)
        {
            return Unreachable(playlists.Problem);
        }

        var sectionKeys = directories.EnumerateArray()
            .Where(directory => string.Equals(String(directory, "type"), sectionType, StringComparison.OrdinalIgnoreCase))
            .Select(directory => String(directory, "key"))
            .OfType<string>()
            .ToArray();
        var smartCollections = await SmartCollectionsAsync(connection, sectionKeys, cancellationToken);

        var watchlist = await WatchlistAsync(connection, cancellationToken);
        return new MediaServerAnswer<MediaServerLibrary>.Reported(new MediaServerLibrary(
            items
                .Select(item => item with
                {
                    SmartCollections = smartCollections is null || item.Level is not (MediaServerItemLevels.Movie or MediaServerItemLevels.Show)
                        ? null
                        : smartCollections.TryGetValue(item.ServerItemId, out var smartNames) ? smartNames : [],
                    Playlists = playlists.Names.TryGetValue(item.ServerItemId, out var names) ? names : [],
                    // Plex's watchlist belongs to the account, not the server. If
                    // plex.tv could not be asked, nobody is said to have it listed.
                    WatchlistedBy = watchlist is null
                        ? null
                        : item.Guid is not null && watchlist.Contains(item.Guid) ? [account] : []
                })
                .ToArray(),
            [account]));
    }

    /// <summary>
    /// Whether the connected account is everyone who can watch on this server. Proven, never assumed: a server's
    /// own /accounts table can lack a shared friend who has not played yet, so it cannot prove absence. Both must
    /// hold, with the owner's token: plex.tv says nobody else has access (no shares of this server, no friends, no
    /// home member besides the owner), and /accounts has exactly one named account, with the connected account's id.
    /// Any call that fails, any other user, a token that is not the owner's, or an id that differs is false, so a
    /// rule about nobody, everyone or none stays undecided and the title is held (#1074).
    /// </summary>
    private async Task<bool> AccountIsTheOnlyOneAsync(MediaServerConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var root = await GetAsync(connection, "/", cancellationToken);
            using var rootDocument = root.Ok ? TryParse(root.Body) : null;
            if (Container(rootDocument) is not { } rootContainer || String(rootContainer, "machineIdentifier") is not { Length: > 0 } machine)
            {
                return false;
            }

            var me = await SendAsync(httpClientFactory, HttpMethod.Get, new Uri("https://plex.tv/api/v2/user"), Headers(connection), "plex.tv", cancellationToken);
            using var meDocument = me.Ok ? TryParse(me.Body) : null;
            var ownerId = meDocument is not null && meDocument.RootElement.ValueKind == JsonValueKind.Object ? Int(meDocument.RootElement, "id") : null;
            if (ownerId is not > 0)
            {
                return false;
            }

            // The token must be the owner's: plex.tv lists this very server as one the account owns.
            if (!await OwnsServerAsync(connection, machine, cancellationToken))
            {
                return false;
            }

            // plex.tv: nobody else has access. Each list must be readable, or it proves nothing.
            var shared = await PlexTvUsersAsync(connection, $"/api/servers/{Uri.EscapeDataString(machine)}/shared_servers", "SharedServer", cancellationToken);
            var friends = await PlexTvUsersAsync(connection, "/api/users", "User", cancellationToken);
            var home = await PlexTvUsersAsync(connection, "/api/home/users", "User", cancellationToken);
            if (shared is not { Count: 0 } || friends is not { Count: 0 } || home is null || home.Any(id => id != ownerId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            {
                return false;
            }

            // The server's own table: exactly one named account (id 0 is the system's), and it is this one.
            var call = await GetAsync(connection, "/accounts", cancellationToken);
            using var document = call.Ok ? TryParse(call.Body) : null;
            if (Container(document) is not { } container ||
                !container.TryGetProperty("Account", out var accounts) ||
                accounts.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var named = accounts.EnumerateArray().Where(entry => Int(entry, "id") is > 0 && !string.IsNullOrWhiteSpace(String(entry, "name"))).ToArray();
            if (named.Length != 1)
            {
                return false;
            }

            // A server's own table keys its owner as id 1 whatever plex.tv's id for that person is (python-plexapi's
            // SystemAccount and Tautulli both treat local id 1 as the owner). So the row is the owner when its id is
            // plex.tv's, or when it is local id 1 and carries the owner's own name.
            var rowId = Int(named[0], "id");
            if (rowId == ownerId)
            {
                return true;
            }

            var rowName = String(named[0], "name")?.Trim();
            return rowId == 1 && rowName is { Length: > 0 } &&
                new[] { "username", "title", "email" }.Any(field =>
                    string.Equals(String(meDocument!.RootElement, field)?.Trim(), rowName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>True only when plex.tv lists a server with this machine identifier as owned by the token's account.</summary>
    private async Task<bool> OwnsServerAsync(MediaServerConnection connection, string machine, CancellationToken cancellationToken)
    {
        var call = await SendAsync(httpClientFactory, HttpMethod.Get, new Uri("https://plex.tv/api/v2/resources?includeHttps=1&includeRelay=1"), Headers(connection), "plex.tv", cancellationToken);
        using var document = call.Ok ? TryParse(call.Body) : null;
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return document.RootElement.EnumerateArray().Any(entry =>
            entry.ValueKind == JsonValueKind.Object &&
            string.Equals(String(entry, "clientIdentifier"), machine, StringComparison.Ordinal) &&
            entry.TryGetProperty("owned", out var owned) && owned.ValueKind == JsonValueKind.True);
    }

    /// <summary>
    /// The ids of the users a plex.tv list holds (its XML elements named <paramref name="element"/>, by their
    /// <c>id</c> or <c>userID</c>); null when the list could not be read.
    /// </summary>
    private async Task<List<string>?> PlexTvUsersAsync(MediaServerConnection connection, string path, string element, CancellationToken cancellationToken)
    {
        var headers = Headers(connection).ToDictionary(pair => pair.Key, pair => pair.Value);
        headers["Accept"] = "application/xml";
        var call = await SendAsync(httpClientFactory, HttpMethod.Get, new Uri("https://plex.tv" + path), headers, "plex.tv", cancellationToken);
        if (!call.Ok)
        {
            return null;
        }

        try
        {
            var root = System.Xml.Linq.XDocument.Parse(call.Body ?? string.Empty).Root;
            if (root is null || !string.Equals(root.Name.LocalName, "MediaContainer", StringComparison.Ordinal))
            {
                return null;
            }

            return root.Elements()
                .Where(node => string.Equals(node.Name.LocalName, element, StringComparison.Ordinal))
                .Select(node => (string?)node.Attribute("userID") ?? (string?)node.Attribute("id") ?? string.Empty)
                .ToList();
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static MediaServerAnswer<MediaServerLibrary> Unreachable(string reason)
        => new MediaServerAnswer<MediaServerLibrary>.Unreachable(reason);

    /// <returns>Null when every page was read; otherwise why it could not be.</returns>
    private async Task<string?> PageAsync(
        MediaServerConnection connection,
        string pathAndQuery,
        Action<JsonElement> each,
        CancellationToken cancellationToken)
    {
        for (var start = 0; ; start += PageSize)
        {
            var page = await GetAsync(connection, $"{pathAndQuery}&X-Plex-Container-Start={start}&X-Plex-Container-Size={PageSize}", cancellationToken);
            using var pageDocument = page.Ok ? TryParse(page.Body) : null;
            if (Container(pageDocument) is not { } container)
            {
                return page.Problem ?? $"{connection.Name} answered with a library listing Deluno could not read.";
            }

            var count = 0;
            if (container.TryGetProperty("Metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in metadata.EnumerateArray())
                {
                    count++;
                    each(entry);
                }
            }

            var total = Int(container, "totalSize") ?? count;
            if (count < PageSize || start + count >= total)
            {
                return null;
            }
        }
    }

    private async Task<(Dictionary<string, List<string>> Names, string? Problem)> PlaylistsAsync(
        MediaServerConnection connection,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var playlists = new List<(string Key, string Title)>();
        var listed = await PageAsync(
            connection,
            "/playlists?playlistType=video",
            entry =>
            {
                if (String(entry, "ratingKey") is { Length: > 0 } key)
                {
                    playlists.Add((key, String(entry, "title") ?? "Playlist"));
                }
            },
            cancellationToken);
        if (listed is not null)
        {
            return (names, listed);
        }

        foreach (var playlist in playlists)
        {
            var members = await PageAsync(
                connection,
                $"/playlists/{Uri.EscapeDataString(playlist.Key)}/items?type=0",
                entry =>
                {
                    if (String(entry, "ratingKey") is not { Length: > 0 } key) return;
                    if (!names.TryGetValue(key, out var list)) names[key] = list = [];
                    if (!list.Contains(playlist.Title, StringComparer.OrdinalIgnoreCase)) list.Add(playlist.Title);
                },
                cancellationToken);
            if (members is not null)
            {
                return (names, members);
            }
        }

        return (names, null);
    }

    /// <summary>
    /// The account's watchlist, as the <c>plex://</c> guids of what is on it.
    /// Null when plex.tv could not be asked: the watchlist is then unknown, not empty.
    /// </summary>
    private async Task<HashSet<string>?> WatchlistAsync(MediaServerConnection connection, CancellationToken cancellationToken)
    {
        var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var start = 0; ; start += PageSize)
        {
            var page = await SendAsync(
                httpClientFactory,
                HttpMethod.Get,
                new Uri($"{WatchlistBaseUrl}/library/sections/watchlist/all?X-Plex-Container-Start={start}&X-Plex-Container-Size={PageSize}"),
                Headers(connection),
                "plex.tv",
                cancellationToken);
            using var document = page.Ok ? TryParse(page.Body) : null;
            if (Container(document) is not { } container)
            {
                return null;
            }

            var count = 0;
            if (container.TryGetProperty("Metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in metadata.EnumerateArray())
                {
                    count++;
                    if (String(entry, "guid") is { Length: > 0 } guid) guids.Add(guid);
                }
            }

            var total = Int(container, "totalSize") ?? count;
            if (count < PageSize || start + count >= total)
            {
                return guids;
            }
        }
    }

    /// <summary>plex.tv's service that holds account watchlists.</summary>
    internal const string WatchlistBaseUrl = "https://discover.provider.plex.tv";

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

        var sectionType = mediaType == "tv" ? "show" : "movie";
        var sectionsCall = await GetAsync(connection, "/library/sections", cancellationToken);
        using var sectionsDocument = sectionsCall.Ok ? TryParse(sectionsCall.Body) : null;
        if (Container(sectionsDocument) is not { } sections ||
            !sections.TryGetProperty("Directory", out var directories) ||
            directories.ValueKind != JsonValueKind.Array)
        {
            return new MediaServerAnswer<MediaServerRefresh>.Unreachable(sectionsCall.Problem ?? $"{connection.Name} did not list its libraries.");
        }

        // Every section of the right type, with the root paths Plex itself
        // sees it at - what a folder is matched against below.
        var candidates = new List<(string Key, string[] Roots)>();
        foreach (var directory in directories.EnumerateArray())
        {
            if (!string.Equals(String(directory, "type"), sectionType, StringComparison.OrdinalIgnoreCase) ||
                String(directory, "key") is not { Length: > 0 } key)
            {
                continue;
            }

            var roots = directory.TryGetProperty("Location", out var locations) && locations.ValueKind == JsonValueKind.Array
                ? locations.EnumerateArray()
                    .Select(location => String(location, "path"))
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => path!)
                    .ToArray()
                : [];
            candidates.Add((key, roots));
        }

        if (candidates.Count == 0)
        {
            return new MediaServerAnswer<MediaServerRefresh>.NotSupported($"{connection.Name} has no {sectionType} library, so there is nothing to refresh.");
        }

        var folders = changes.Select(change => FolderOf(change.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // The longest matching root wins, the same rule a download-client path
        // mapping uses: a specific mapping may safely sit beside a broader one.
        var resolved = new List<(string Key, string Folder)>();
        var unresolved = new List<string>();
        foreach (var folder in folders)
        {
            var normalizedFolder = NormalizeForMatch(folder);
            var match = candidates
                .SelectMany(candidate => candidate.Roots.Select(root => (candidate.Key, NormalizedRoot: NormalizeForMatch(root))))
                .Where(entry => normalizedFolder.Equals(entry.NormalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                                normalizedFolder.StartsWith(entry.NormalizedRoot + "/", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.NormalizedRoot.Length)
                .Select(entry => (string?)entry.Key)
                .FirstOrDefault();

            if (match is null)
            {
                unresolved.Add(folder);
            }
            else
            {
                resolved.Add((match, folder));
            }
        }

        string? problem = null;
        var usedFallback = false;
        var touchedSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, folder) in resolved)
        {
            var refresh = await GetAsync(connection, $"/library/sections/{Uri.EscapeDataString(key)}/refresh?path={Uri.EscapeDataString(folder)}", cancellationToken);
            if (refresh.Ok)
            {
                touchedSections.Add(key);
            }
            else
            {
                problem ??= refresh.Problem;
            }
        }

        if (unresolved.Count > 0)
        {
            // Nothing on the server maps to these folders - most often a
            // missing path mapping. A full scan of every section of this type
            // is the fallback the audit calls for, rather than silently
            // telling the server nothing at all.
            usedFallback = true;
            foreach (var candidate in candidates)
            {
                var refresh = await GetAsync(connection, $"/library/sections/{Uri.EscapeDataString(candidate.Key)}/refresh", cancellationToken);
                if (refresh.Ok)
                {
                    touchedSections.Add(candidate.Key);
                }
                else
                {
                    problem ??= refresh.Problem;
                }
            }
        }

        if (changes.Any(change => change.Kind == MediaServerChangeKind.Deleted))
        {
            foreach (var key in touchedSections)
            {
                // Best effort: a scan already told Plex the file is gone, and
                // a trash that will not empty should not turn a successful
                // refresh into a failed one.
                await SendAsync(httpClientFactory, HttpMethod.Put, Address(connection, $"/library/sections/{Uri.EscapeDataString(key)}/emptyTrash"), Headers(connection), connection.Name, cancellationToken);
            }
        }

        if (touchedSections.Count == 0)
        {
            return new MediaServerAnswer<MediaServerRefresh>.Unreachable(problem ?? $"{connection.Name} could not be refreshed.");
        }

        return new MediaServerAnswer<MediaServerRefresh>.Reported(new MediaServerRefresh(
            usedFallback
                ? $"{connection.Name} could not map every changed path to a library, so its {sectionType} libraries were fully rescanned instead."
                : $"{connection.Name} was told about {folders.Length} changed folder{(folders.Length == 1 ? string.Empty : "s")}.",
            usedFallback));
    }

    /// <summary>
    /// Whether Plex can see a folder. Plex's folder browser takes the folder as the base64 of its path in the address
    /// (<c>/services/browse/{base64}</c>) and ignores a <c>path</c> query, listing its own roots for any path whatever (recorded from
    /// the real server), so the folder is looked for in its parent's listing.
    /// </summary>
    public async Task<MediaServerAnswer<MediaServerPathMappingTest>> TestPathAsync(
        MediaServerConnection connection,
        string serverPath,
        CancellationToken cancellationToken)
    {
        var wanted = NormalizeForMatch(serverPath);
        var parent = ParentOf(serverPath);
        var browse = await GetAsync(connection, $"/services/browse/{Uri.EscapeDataString(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(parent ?? serverPath)))}?includeFiles=0", cancellationToken);
        if (!browse.Ok)
        {
            return new MediaServerAnswer<MediaServerPathMappingTest>.Unreachable(browse.Problem!);
        }

        using var document = TryParse(browse.Body);
        var container = Container(document);
        // A root folder has no parent to look in: the server answering with a listing is the answer.
        var found = container is not null
            && (parent is null
                || (container.Value.TryGetProperty("Path", out var entries) && entries.ValueKind == JsonValueKind.Array
                    && entries.EnumerateArray().Any(entry => String(entry, "path") is { } path && string.Equals(NormalizeForMatch(path), wanted, StringComparison.OrdinalIgnoreCase))));
        return new MediaServerAnswer<MediaServerPathMappingTest>.Reported(new MediaServerPathMappingTest(
            found,
            found
                ? $"{connection.Name} can see {serverPath}."
                : $"{connection.Name} answered, but {serverPath} is not among the folders it lists. Check the mapped path."));
    }

    /// <summary>The folder holding <paramref name="path"/>, or null for a root ("/", "C:\").</summary>
    private static string? ParentOf(string path)
    {
        var trimmed = path.Trim().TrimEnd('/', '\\');
        var separator = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        if (trimmed.Length == 0 || separator < 0 || (trimmed.Length == 2 && trimmed[1] == ':'))
        {
            return null;
        }

        return separator == 0 ? "/" : trimmed[..separator];
    }

    private static string NormalizeForMatch(string path) => path.Trim().TrimEnd('/', '\\').Replace('\\', '/');

    public async Task<MediaServerAnswer<MediaServerCollectionSync>> SyncCollectionAsync(
        MediaServerConnection connection,
        MediaServerCollectionRequest request,
        CancellationToken cancellationToken)
    {
        // A Plex collection lives in one library section and holds one kind of
        // item, so there is one per section: films in a movie section, shows in
        // a show section. A season or episode cannot sit in a show collection.
        var sectionType = request.MediaType == "tv" ? "show" : "movie";
        var itemLevel = request.MediaType == "tv" ? MediaServerItemLevels.Show : MediaServerItemLevels.Movie;
        var collectionType = request.MediaType == "tv" ? 2 : 1;
        var usable = request.Members.Where(member => member.Level == itemLevel && !string.IsNullOrWhiteSpace(member.SectionId)).ToArray();
        var skipped = request.Members.Count - usable.Length;

        var root = await GetAsync(connection, "/", cancellationToken);
        using var rootDocument = root.Ok ? TryParse(root.Body) : null;
        if (Container(rootDocument) is not { } rootContainer || String(rootContainer, "machineIdentifier") is not { Length: > 0 } machine)
        {
            return new MediaServerAnswer<MediaServerCollectionSync>.Unreachable(root.Problem ?? $"{connection.Name} did not say which server it is.");
        }

        var sectionsCall = await GetAsync(connection, "/library/sections", cancellationToken);
        using var sectionsDocument = sectionsCall.Ok ? TryParse(sectionsCall.Body) : null;
        if (Container(sectionsDocument) is not { } sections ||
            !sections.TryGetProperty("Directory", out var directories) ||
            directories.ValueKind != JsonValueKind.Array)
        {
            return new MediaServerAnswer<MediaServerCollectionSync>.Unreachable(sectionsCall.Problem ?? $"{connection.Name} did not list its libraries.");
        }

        var sectionKeys = directories.EnumerateArray()
            .Where(directory => string.Equals(String(directory, "type"), sectionType, StringComparison.OrdinalIgnoreCase))
            .Select(directory => String(directory, "key"))
            .OfType<string>()
            .ToArray();

        string MetadataUri(IEnumerable<string> keys)
            => Uri.EscapeDataString($"server://{machine}/com.plexapp.plugins.library/library/metadata/{string.Join(',', keys)}");

        int added = 0, removed = 0, members = 0;
        var deleted = false;
        foreach (var section in sectionKeys)
        {
            var wanted = usable.Where(member => member.SectionId == section).Select(member => member.ItemId).Distinct(StringComparer.Ordinal).ToArray();
            var existing = new List<string>();
            var listed = await PageAsync(
                connection,
                $"/library/sections/{Uri.EscapeDataString(section)}/collections?includeGuids=0",
                entry =>
                {
                    if (string.Equals(String(entry, "title"), request.Name, StringComparison.OrdinalIgnoreCase) && String(entry, "ratingKey") is { } key)
                    {
                        existing.Add(key);
                    }
                },
                cancellationToken);
            if (listed is not null)
            {
                return new MediaServerAnswer<MediaServerCollectionSync>.Unreachable(listed);
            }

            if (wanted.Length == 0)
            {
                foreach (var key in existing)
                {
                    var gone = await SendAsync(httpClientFactory, HttpMethod.Delete, Address(connection, $"/library/collections/{Uri.EscapeDataString(key)}"), Headers(connection), connection.Name, cancellationToken);
                    if (!gone.Ok)
                    {
                        return new MediaServerAnswer<MediaServerCollectionSync>.Unreachable(gone.Problem!);
                    }

                    deleted = true;
                }

                continue;
            }

            members += wanted.Length;
            var collection = existing.FirstOrDefault();
            if (collection is null)
            {
                var created = await SendAsync(
                    httpClientFactory,
                    HttpMethod.Post,
                    Address(connection, $"/library/collections?type={collectionType}&title={Uri.EscapeDataString(request.Name)}&smart=0&sectionId={Uri.EscapeDataString(section)}&uri={MetadataUri(wanted)}"),
                    Headers(connection),
                    connection.Name,
                    cancellationToken);
                using var createdDocument = created.Ok ? TryParse(created.Body) : null;
                if (Container(createdDocument) is not { } createdContainer ||
                    !createdContainer.TryGetProperty("Metadata", out var createdMetadata) ||
                    createdMetadata.ValueKind != JsonValueKind.Array ||
                    createdMetadata.GetArrayLength() == 0 ||
                    String(createdMetadata[0], "ratingKey") is not { } createdKey)
                {
                    return new MediaServerAnswer<MediaServerCollectionSync>.Unreachable(created.Problem ?? $"{connection.Name} did not say which collection it created.");
                }

                collection = createdKey;
                added += wanted.Length;
                // Best effort: a collection without its summary still works.
                await SendAsync(
                    httpClientFactory,
                    HttpMethod.Put,
                    Address(connection, $"/library/sections/{Uri.EscapeDataString(section)}/all?type=18&id={Uri.EscapeDataString(collection)}&summary.value={Uri.EscapeDataString(request.Description)}&summary.locked=1"),
                    Headers(connection),
                    connection.Name,
                    cancellationToken);
            }
            else
            {
                var current = new HashSet<string>(StringComparer.Ordinal);
                var read = await PageAsync(
                    connection,
                    $"/library/collections/{Uri.EscapeDataString(collection)}/children?excludeAllLeaves=1",
                    entry =>
                    {
                        if (String(entry, "ratingKey") is { } key) current.Add(key);
                    },
                    cancellationToken);
                if (read is not null)
                {
                    return new MediaServerAnswer<MediaServerCollectionSync>.Unreachable(read);
                }

                var add = wanted.Where(key => !current.Contains(key)).ToArray();
                if (add.Length > 0)
                {
                    var put = await SendAsync(httpClientFactory, HttpMethod.Put, Address(connection, $"/library/collections/{Uri.EscapeDataString(collection)}/items?uri={MetadataUri(add)}"), Headers(connection), connection.Name, cancellationToken);
                    if (!put.Ok)
                    {
                        return new MediaServerAnswer<MediaServerCollectionSync>.Unreachable(put.Problem!);
                    }

                    added += add.Length;
                }

                foreach (var key in current.Where(key => !wanted.Contains(key, StringComparer.Ordinal)))
                {
                    var taken = await SendAsync(httpClientFactory, HttpMethod.Delete, Address(connection, $"/library/collections/{Uri.EscapeDataString(collection)}/items/{Uri.EscapeDataString(key)}"), Headers(connection), connection.Name, cancellationToken);
                    if (!taken.Ok)
                    {
                        return new MediaServerAnswer<MediaServerCollectionSync>.Unreachable(taken.Problem!);
                    }

                    removed++;
                }
            }

            var home = await PromoteAsync(connection, section, collection, request.PromoteToHome, cancellationToken);
            if (home is not null)
            {
                return new MediaServerAnswer<MediaServerCollectionSync>.Unreachable(home);
            }
        }

        return new MediaServerAnswer<MediaServerCollectionSync>.Reported(new MediaServerCollectionSync(added, removed, members, skipped, deleted));
    }

    /// <summary>
    /// Shows the collection on home, or stops showing it, to match the server's
    /// setting. Asked first, so an unchanged setting sends nothing.
    /// </summary>
    /// <returns>Null when done; otherwise why not.</returns>
    private async Task<string?> PromoteAsync(MediaServerConnection connection, string section, string collection, bool promote, CancellationToken cancellationToken)
    {
        var manage = await GetAsync(connection, $"/hubs/sections/{Uri.EscapeDataString(section)}/manage?metadataItemId={Uri.EscapeDataString(collection)}", cancellationToken);
        using var document = manage.Ok ? TryParse(manage.Body) : null;
        if (Container(document) is not { } container)
        {
            return manage.Problem ?? $"{connection.Name} did not say where its collections are shown.";
        }

        string? identifier = null;
        var promoted = false;
        if (container.TryGetProperty("Hub", out var hubs) && hubs.ValueKind == JsonValueKind.Array)
        {
            foreach (var hub in hubs.EnumerateArray())
            {
                identifier = String(hub, "identifier") ?? identifier;
                promoted |= Bool(hub, "promotedToOwnHome");
            }
        }

        if (promote == promoted)
        {
            return null;
        }

        MediaServerCallResult? result = promote
            ? await SendAsync(httpClientFactory, HttpMethod.Post, Address(connection, $"/hubs/sections/{Uri.EscapeDataString(section)}/manage?metadataItemId={Uri.EscapeDataString(collection)}&promotedToRecommended=1&promotedToOwnHome=1&promotedToSharedHome=1"), Headers(connection), connection.Name, cancellationToken)
            : identifier is null
                ? null
                : await SendAsync(httpClientFactory, HttpMethod.Delete, Address(connection, $"/hubs/sections/{Uri.EscapeDataString(section)}/manage/{Uri.EscapeDataString(identifier)}"), Headers(connection), connection.Name, cancellationToken);
        return result is null || result.Ok ? null : result.Problem;
    }

    internal static string FolderOf(string path)
    {
        var separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return separator > 0 ? path[..separator] : path;
    }

    private static MediaServerItem? ToItem(JsonElement entry, string mediaType, string sectionKey, string account, bool accountIsEveryone = false)
    {
        if (String(entry, "ratingKey") is not { Length: > 0 } ratingKey)
        {
            return null;
        }

        var level = String(entry, "type") switch
        {
            "show" => MediaServerItemLevels.Show,
            "season" => MediaServerItemLevels.Season,
            "episode" => MediaServerItemLevels.Episode,
            _ => MediaServerItemLevels.Movie
        };

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
        var video = MediaServerVideo.None;
        if (entry.TryGetProperty("Media", out var media) && media.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in media.EnumerateArray()
                         .Where(item => item.TryGetProperty("Part", out var parts) && parts.ValueKind == JsonValueKind.Array)
                         .SelectMany(item => item.GetProperty("Part").EnumerateArray()))
            {
                if (String(part, "file") is { Length: > 0 } file) paths.Add(file);
            }

            if (media.GetArrayLength() > 0)
            {
                var first = media[0];
                video = new MediaServerVideo(
                    PlexResolution(String(first, "videoResolution")),
                    Long(first, "bitrate"),
                    String(first, "videoCodec"));
            }
        }

        if (entry.TryGetProperty("Location", out var locations) && locations.ValueKind == JsonValueKind.Array)
        {
            foreach (var location in locations.EnumerateArray())
            {
                if (String(location, "path") is { Length: > 0 } folder) paths.Add(folder);
            }
        }

        // A film or episode is watched once its view count is set; a show or a
        // season once every episode in it has been.
        var views = Int(entry, "viewCount") ?? 0;
        var played = level is MediaServerItemLevels.Show or MediaServerItemLevels.Season
            ? Int(entry, "leafCount") is > 0 and var leaves && Int(entry, "viewedLeafCount") >= leaves
            : views > 0;
        var lastViewed = Long(entry, "lastViewedAt") is > 0 and var seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : (DateTimeOffset?)null;
        var added = Long(entry, "addedAt") is > 0 and var addedSeconds ? DateTimeOffset.FromUnixTimeSeconds(addedSeconds) : (DateTimeOffset?)null;
        var inProgress = !played && (Long(entry, "viewOffset") ?? 0) > 0;
        var userRating = Double(entry, "userRating");
        // Plex has no separate favourite flag; a maxed-out rating (10, the
        // "loved" state Plexamp shows as a heart) is its favourite signal.
        var favourite = userRating is double rating ? rating >= 10 : (bool?)null;
        var viewer = played || inProgress || views > 0 || lastViewed is not null || userRating is not null
            ? new MediaServerViewer(account, played, views, lastViewed, inProgress, favourite, userRating)
            : null;

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
            new MediaServerWatchState(played, [], lastViewed, PerUserKnown: accountIsEveryone),
            sectionKey)
        {
            Level = level,
            Guid = String(entry, "guid"),
            ShowServerItemId = level switch
            {
                MediaServerItemLevels.Season => String(entry, "parentRatingKey"),
                MediaServerItemLevels.Episode => String(entry, "grandparentRatingKey"),
                _ => null
            },
            SeasonServerItemId = level == MediaServerItemLevels.Episode ? String(entry, "parentRatingKey") : null,
            SeasonNumber = level == MediaServerItemLevels.Season ? Int(entry, "index") : level == MediaServerItemLevels.Episode ? Int(entry, "parentIndex") : null,
            EpisodeNumber = level == MediaServerItemLevels.Episode ? Int(entry, "index") : null,
            ReleaseDate = Date(entry, "originallyAvailableAt"),
            Genres = Names(entry, "Genre", "tag"),
            Labels = Names(entry, "Label", "tag"),
            Collections = Names(entry, "Collection", "tag"),
            Ratings = new MediaServerRatings(Critic: Double(entry, "rating"), Audience: Double(entry, "audienceRating")),
            Video = video,
            AudioLanguages = PlexAudioLanguages(entry),
            Runtime = Long(entry, "duration") is > 0 and var milliseconds ? TimeSpan.FromMilliseconds(milliseconds) : null,
            Viewers = viewer is null ? [] : [viewer]
        };
    }

    /// <summary>
    /// The audio tracks' languages, from the streams of the first file. Null when
    /// the entry carries no streams (a bulk listing does not), or a track has no tag.
    /// </summary>
    internal static IReadOnlyList<string>? PlexAudioLanguages(JsonElement entry)
    {
        if (!entry.TryGetProperty("Media", out var media) || media.ValueKind != JsonValueKind.Array || media.GetArrayLength() == 0)
        {
            return null;
        }

        // Every version and every part of it: a second version may hold the
        // track the first lacks, and a version whose streams are missing makes the
        // whole answer unknown.
        var tags = new List<string?>();
        foreach (var version in media.EnumerateArray())
        {
            if (!version.TryGetProperty("Part", out var parts) || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0)
            {
                return null;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (!part.TryGetProperty("Stream", out var streams) || streams.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                tags.AddRange(streams.EnumerateArray()
                    .Where(stream => Long(stream, "streamType") == 2)
                    .Select(stream => String(stream, "languageCode") ?? String(stream, "language")));
            }
        }

        return MediaServerLanguages.OfAudioTracks(tags);
    }

    /// <summary>
    /// Whether a collection is smart: true, "true", "1" or 1; false, "false", "0"
    /// or 0 (or no flag, which Plex leaves off an ordinary one). Anything else is null: not known.
    /// </summary>
    internal static bool? SmartFlag(JsonElement entry)
    {
        if (!entry.TryGetProperty("smart", out var value))
        {
            return false;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number when value.TryGetInt32(out var number):
                return number == 1 ? true : number == 0 ? false : null;
            case JsonValueKind.String:
                var text = value.GetString()?.Trim();
                return text is not null && (text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase))
                    ? true
                    : text is not null && (text == "0" || text.Equals("false", StringComparison.OrdinalIgnoreCase)) ? false : null;
            default:
                return null;
        }
    }

    /// <summary>What is playing now: Plex's own sessions list, with each play's season, show, file and provider ids (#1012).</summary>
    public async Task<MediaServerAnswer<IReadOnlyList<MediaServerPlay>>> ListPlayingAsync(MediaServerConnection connection, CancellationToken cancellationToken)
    {
        var call = await GetAsync(connection, "/status/sessions", cancellationToken);
        using var document = call.Ok ? TryParse(call.Body) : null;
        if (Container(document) is not { } container)
        {
            return new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Unreachable(call.Problem ?? $"{connection.Name} did not say what is being played.");
        }

        var plays = new List<MediaServerPlay>();
        var shows = new Dictionary<string, (string? Tmdb, string? Tvdb, string? Imdb)>(StringComparer.Ordinal);
        if (container.TryGetProperty("Metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in metadata.EnumerateArray())
            {
                var ids = new[] { "ratingKey", "parentRatingKey", "grandparentRatingKey" }
                    .Select(name => String(entry, name))
                    .OfType<string>()
                    .Where(id => id.Length > 0)
                    .ToArray();
                var paths = new List<string>();
                if (entry.TryGetProperty("Media", out var media) && media.ValueKind == JsonValueKind.Array)
                {
                    foreach (var version in media.EnumerateArray())
                    {
                        if (!version.TryGetProperty("Part", out var parts) || parts.ValueKind != JsonValueKind.Array) continue;
                        paths.AddRange(parts.EnumerateArray().Select(part => String(part, "file")).OfType<string>().Where(file => file.Length > 0));
                    }
                }

                string? Guid(string scheme)
                {
                    if (!entry.TryGetProperty("Guid", out var guids) || guids.ValueKind != JsonValueKind.Array) return null;
                    foreach (var guid in guids.EnumerateArray())
                    {
                        if (String(guid, "id") is { } text && text.StartsWith(scheme + "://", StringComparison.OrdinalIgnoreCase))
                        {
                            return text[(scheme.Length + 3)..];
                        }
                    }

                    return null;
                }

                var isEpisode = String(entry, "type") == "episode";
                (string? Tmdb, string? Tvdb, string? Imdb) show = default;
                if (isEpisode && String(entry, "grandparentRatingKey") is { Length: > 0 } showKey && !shows.TryGetValue(showKey, out show))
                {
                    // A session carries the episode's own Guid list, not the show's: ask for the show once.
                    show = await ReadShowGuidsAsync(connection, showKey, cancellationToken);
                    shows[showKey] = show;
                }

                plays.Add(new MediaServerPlay(
                    ids, paths, String(entry, "type") == "movie", Guid("tmdb"), Guid("tvdb"), Guid("imdb"),
                    isEpisode, String(entry, "grandparentTitle"), show.Tmdb, show.Tvdb, show.Imdb,
                    Int(entry, "parentIndex"), Int(entry, "index")));
            }
        }

        return new MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported(plays);
    }

    private async Task<(string? Tmdb, string? Tvdb, string? Imdb)> ReadShowGuidsAsync(MediaServerConnection connection, string showKey, CancellationToken cancellationToken)
    {
        // Not being able to ask leaves the ids null, which the caller reads as "cannot tell which show", never "not this show".
        var call = await GetAsync(connection, $"/library/metadata/{Uri.EscapeDataString(showKey)}", cancellationToken);
        using var document = call.Ok ? TryParse(call.Body) : null;
        if (Container(document) is not { } container ||
            !container.TryGetProperty("Metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Array || metadata.GetArrayLength() == 0 ||
            !metadata[0].TryGetProperty("Guid", out var guids) || guids.ValueKind != JsonValueKind.Array)
        {
            return default;
        }

        string? Guid(string scheme)
        {
            foreach (var guid in guids.EnumerateArray())
            {
                if (String(guid, "id") is { } text && text.StartsWith(scheme + "://", StringComparison.OrdinalIgnoreCase))
                {
                    return text[(scheme.Length + 3)..];
                }
            }

            return null;
        }

        return (Guid("tmdb"), Guid("tvdb"), Guid("imdb"));
    }

    /// <summary>
    /// Episodes' audio languages, asked for a batch at a time: the bulk listing
    /// leaves streams out, and only a rule that reads them pays for this.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ReadAudioLanguagesAsync(
        MediaServerConnection connection,
        IReadOnlyList<string> serverItemIds,
        CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in serverItemIds.Chunk(50))
        {
            var call = await GetAsync(connection, $"/library/metadata/{string.Join(',', batch.Select(Uri.EscapeDataString))}", cancellationToken);
            using var document = call.Ok ? TryParse(call.Body) : null;
            if (Container(document) is not { } container ||
                !container.TryGetProperty("Metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in metadata.EnumerateArray())
            {
                if (String(entry, "ratingKey") is { Length: > 0 } key && PlexAudioLanguages(entry) is { } languages)
                {
                    found[key] = languages;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Which titles are in which smart collections (#1007), by ratingKey. A smart
    /// collection is a saved search: the items do not carry its tag, so each
    /// smart collection's members are read. Null when it could not be read in
    /// full, which is "not known", never "in none".
    /// </summary>
    private async Task<Dictionary<string, List<string>>?> SmartCollectionsAsync(
        MediaServerConnection connection,
        IReadOnlyList<string> sectionKeys,
        CancellationToken cancellationToken)
    {
        var members = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var unreadable = false;
        foreach (var section in sectionKeys)
        {
            var smart = new List<(string Key, string Title)>();
            var listed = await PageAsync(
                connection,
                $"/library/sections/{Uri.EscapeDataString(section)}/collections?includeGuids=0",
                entry =>
                {
                    var flag = SmartFlag(entry);
                    if (flag is null)
                    {
                        // A collection whose kind cannot be read might be a smart one.
                        unreadable = true;
                        return;
                    }

                    if (flag == true && String(entry, "ratingKey") is { Length: > 0 } key)
                    {
                        smart.Add((key, String(entry, "title") ?? "Smart collection"));
                    }
                },
                cancellationToken);
            if (listed is not null || unreadable)
            {
                return null;
            }

            foreach (var collection in smart)
            {
                var children = await PageAsync(
                    connection,
                    $"/library/collections/{Uri.EscapeDataString(collection.Key)}/children?excludeAllLeaves=1",
                    entry =>
                    {
                        if (String(entry, "ratingKey") is not { Length: > 0 } key) return;
                        if (!members.TryGetValue(key, out var list)) members[key] = list = [];
                        if (!list.Contains(collection.Title, StringComparer.OrdinalIgnoreCase)) list.Add(collection.Title);
                    },
                    cancellationToken);
                if (children is not null)
                {
                    return null;
                }
            }
        }

        return members;
    }

    /// <summary>A film's or show's own page: every person, tag and score.</summary>
    private static MediaServerItem WithDetail(MediaServerItem item, JsonElement detail)
    {
        double? imdb = null, rtCritic = null, rtAudience = null, tmdb = null;
        if (detail.TryGetProperty("Rating", out var ratings) && ratings.ValueKind == JsonValueKind.Array)
        {
            foreach (var rating in ratings.EnumerateArray())
            {
                var image = String(rating, "image") ?? string.Empty;
                var value = Double(rating, "value");
                var type = String(rating, "type");
                if (image.StartsWith("imdb://", StringComparison.OrdinalIgnoreCase)) imdb = value;
                else if (image.StartsWith("themoviedb://", StringComparison.OrdinalIgnoreCase)) tmdb = value;
                else if (image.StartsWith("rottentomatoes://", StringComparison.OrdinalIgnoreCase))
                {
                    if (type == "critic") rtCritic = value;
                    else rtAudience = value;
                }
            }
        }

        var people = new List<string>();
        foreach (var role in new[] { "Role", "Director", "Writer", "Producer" })
        {
            people.AddRange(Names(detail, role, "tag").Where(name => !people.Contains(name, StringComparer.OrdinalIgnoreCase)));
        }

        return item with
        {
            Genres = Names(detail, "Genre", "tag") is { Count: > 0 } genres ? genres : item.Genres,
            Labels = Names(detail, "Label", "tag") is { Count: > 0 } labels ? labels : item.Labels,
            Collections = Names(detail, "Collection", "tag") is { Count: > 0 } collections ? collections : item.Collections,
            People = people,
            AudioLanguages = PlexAudioLanguages(detail) ?? item.AudioLanguages,
            Ratings = item.Ratings with { Imdb = imdb, RottenTomatoesCritic = rtCritic, RottenTomatoesAudience = rtAudience, Tmdb = tmdb }
        };
    }

    /// <summary>Plex says <c>4k</c>, <c>1080</c>, <c>720</c>, <c>480</c>, <c>sd</c>.</summary>
    private static string? PlexResolution(string? value)
        => value?.ToLowerInvariant() switch
        {
            null or "" => null,
            "4k" => "2160p",
            "sd" => "SD",
            var digits when int.TryParse(digits, out _) => $"{digits}p",
            var other => other
        };

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
