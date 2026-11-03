using System.Net;
using System.Text.Json.Nodes;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.MediaServers;

/// <summary>
/// #493: Plex, Jellyfin and Emby, read the way each one actually answers - and
/// "could not ask" never mistaken for "nothing there".
/// </summary>
public sealed class MediaServerClientTests
{
    private static MediaServerConnection Server(string kind, string baseUrl = "http://192.168.1.51:8096")
        => new("server-1", kind, "Rig " + kind, baseUrl, "token-123", true, "untested", null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    /// <summary>
    /// Jellyfin 12.4.0's own replies for three people (the administrator, Ben, and Old who is turned off), recorded after the administrator and Old
    /// played the first film in the list, Ben favourited it and stopped partway (tests/fixtures/recorded/jellyfin/users-several, #1272).
    /// </summary>
    [Fact]
    public async Task Jellyfin_combines_what_every_user_has_watched()
    {
        var users = RecordedReplies.Jellyfin("users-several");
        var people = users.Exchanges[0].Reply.Json().AsArray();
        string IdOf(string name) => people.Single(person => person!["Name"]!.GetValue<string>() == name)!["Id"]!.GetValue<string>();
        var benId = IdOf("Ben");
        var oldId = IdOf("Old");
        var ownerName = Assert.Single(people, person => person!["Name"]!.GetValue<string>() is not ("Ben" or "Old"))!["Name"]!.GetValue<string>();
        var requests = new List<string>();
        var client = new JellyfinMediaServerClient(Factory(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            // Jellyfin 12 refuses the legacy X-Emby-Token header (#493, found on the rig).
            Assert.Equal("MediaBrowser Token=\"token-123\"", Assert.Single(request.Headers.GetValues("Authorization")));
            Assert.False(request.Headers.Contains("X-Emby-Token"));
            var path = request.RequestUri!.AbsolutePath;
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (path == "/Users") return users.Exchanges[0].Reply.ToResponse();
            // The film listing comes from the same server's second page, where the film sits among the others.
            if (path == "/Items") return RecordedReplies.Jellyfin("catalogue-paged").Exchanges[1].Reply.ToResponse();
            if (path.StartsWith("/Users/", StringComparison.Ordinal))
            {
                var filter = query.Contains("IsPlayed", StringComparison.Ordinal) ? "IsPlayed" : query.Contains("IsFavorite", StringComparison.Ordinal) ? "IsFavorite" : query.Contains("IsResumable", StringComparison.Ordinal) ? "IsResumable" : null;
                // No collections or playlists exist on this server: the recorded empty listing.
                if (filter is null) return users.Exchanges[2].Reply.ToResponse();
                var recorded = users.Exchanges.Skip(1).Single(exchange => exchange.Request.Path.StartsWith(path, StringComparison.Ordinal) && exchange.Request.Path.Contains(filter, StringComparison.Ordinal));
                return recorded.Reply.ToResponse();
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Jellyfin), "movies", CancellationToken.None);

        var library = Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value;
        Assert.Equal(2, library.Users.Count);
        Assert.Contains("Ben", library.Users);
        Assert.Contains(ownerName, library.Users);
        Assert.DoesNotContain("Old", library.Users);
        var sintel = Assert.Single(library.Items, item => item.Title == "Recorded Locked (2022)");
        Assert.Equal(MediaServerItemLevels.Movie, sintel.Level);
        Assert.True(sintel.Watch.PlayedByAnyone);
        Assert.True(sintel.Watch.PerUserKnown);
        // The administrator played it; Old did too, but a user who is turned off is not asked.
        Assert.Equal([ownerName], sintel.Watch.PlayedBy);
        Assert.Contains(sintel.Viewers, viewer => viewer is { User: "Ben", Favourite: true, InProgress: true });
        Assert.DoesNotContain(requests, request => request.StartsWith($"/Users/{oldId}/", StringComparison.Ordinal));
        Assert.Contains(requests, request => request.StartsWith($"/Users/{benId}/", StringComparison.Ordinal));
    }

    /// <summary>The kind's recorded state, Jellyfin 12.4.0 or Emby 4.10.1.0 (they answer the same calls the same way, Emby under /emby).</summary>
    private static Recording Recorded(string kind, string state)
        => kind == MediaServerKinds.Emby ? RecordedReplies.Emby(state) : RecordedReplies.Jellyfin(state);

    /// <summary>
    /// The recorded reply to the kind of call Deluno made to Jellyfin or Emby: the lists of users and libraries, the catalogue by the item
    /// types asked for, the show lookup and the sessions. Null for a call no recording answers.
    /// </summary>
    private static HttpResponseMessage? FromRecordings(string kind, HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.StartsWith("/emby/", StringComparison.Ordinal)) path = path["/emby".Length..];
        var query = Uri.UnescapeDataString(request.RequestUri.Query);
        Exchange? exchange = null;
        if (path == "/System/Info") exchange = Recorded(kind, "identity").Exchanges[1];
        else if (path == "/Library/VirtualFolders") exchange = Recorded(kind, "libraries").Exchanges[0];
        else if (path == "/Users") exchange = Recorded(kind, "libraries").Exchanges[1];
        else if (path == "/Sessions") exchange = Recorded(kind, "sessions-playing").Exchanges[2];
        else if (path == "/Items" && query.Contains("Ids=", StringComparison.Ordinal)) exchange = Recorded(kind, "catalogue-series").Exchanges[1];
        else if (path == "/Items" && query.Contains("IncludeItemTypes=Movie", StringComparison.Ordinal)) exchange = Recorded(kind, "catalogue-movies").Exchanges[0];
        else if (path == "/Items" && query.Contains("IncludeItemTypes=Series", StringComparison.Ordinal)) exchange = Recorded(kind, "catalogue-series").Exchanges[0];
        else if (path.StartsWith("/Users/", StringComparison.Ordinal) && path.EndsWith("/Items", StringComparison.Ordinal))
            exchange = Recorded(kind, "watched").Exchanges[query.Contains("IsPlayed=true", StringComparison.Ordinal) ? 1 : 2];
        return exchange?.Reply.ToResponse();
    }

    private static EmbyFamilyMediaServerClient ClientOf(string kind, IHttpClientFactory factory)
        => kind == MediaServerKinds.Emby ? new EmbyMediaServerClient(factory) : new JellyfinMediaServerClient(factory);

    /// <summary>Jellyfin 12.4.0's TV listing: the recorded show, its season and its two episodes, with the levels, ids and numbers the recording holds.</summary>
    [Fact]
    public async Task A_Jellyfin_TV_listing_holds_shows_seasons_and_episodes()
    {
        var recorded = RecordedReplies.Jellyfin("catalogue-series").Exchanges[0].Reply.Json()["Items"]!.AsArray();
        JsonNode Of(string type) => recorded.First(item => item!["Type"]!.GetValue<string>() == type)!;
        var query = "";
        var client = new JellyfinMediaServerClient(Factory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Items" && request.RequestUri.Query.Contains("Series", StringComparison.Ordinal)) query = Uri.UnescapeDataString(request.RequestUri.Query);
            return FromRecordings(MediaServerKinds.Jellyfin, request) ?? throw new InvalidOperationException($"No recording answers {request.RequestUri!.PathAndQuery}.");
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Jellyfin), "tv", CancellationToken.None);

        Assert.Contains("IncludeItemTypes=Series,Season,Episode", query, StringComparison.Ordinal);
        var items = Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value.Items;
        var showId = Of("Series")["Id"]!.GetValue<string>();
        Assert.Equal(MediaServerItemLevels.Show, Assert.Single(items, item => item.ServerItemId == showId).Level);
        var seasonId = Of("Season")["Id"]!.GetValue<string>();
        var season = Assert.Single(items, item => item.ServerItemId == seasonId);
        Assert.Equal((MediaServerItemLevels.Season, showId, 1), (season.Level, season.ShowServerItemId, season.SeasonNumber));
        var episodeId = Of("Episode")["Id"]!.GetValue<string>();
        var episode = Assert.Single(items, item => item.ServerItemId == episodeId);
        Assert.Equal((MediaServerItemLevels.Episode, showId, seasonId, 1, 1), (episode.Level, episode.ShowServerItemId, episode.SeasonServerItemId, episode.SeasonNumber, episode.EpisodeNumber));
        Assert.Equal(2, items.Count(item => item.Level == MediaServerItemLevels.Episode));
    }

    /// <summary>Jellyfin 12.4.0 before any library holds a film: its own empty listing (tests/fixtures/recorded/jellyfin/library-empty) is an answer, not a failure.</summary>
    [Fact]
    public async Task An_empty_library_is_an_answer()
    {
        var empty = RecordedReplies.Jellyfin("library-empty");
        var client = new JellyfinMediaServerClient(Factory(request => request.RequestUri!.AbsolutePath == "/Users" ? empty.Exchanges[1].Reply.ToResponse() : empty.Exchanges[2].Reply.ToResponse()));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Jellyfin), "movies", CancellationToken.None);

        Assert.Empty(Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value.Items);
    }

    /// <summary>
    /// Emby 4.10.1.0's own refusal of a key it does not know (a 401), and the same reply with only the status changed to 500 for a server
    /// that fails: neither is an empty library.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_server_that_refuses_or_fails_could_not_be_asked(HttpStatusCode code)
    {
        var refused = RecordedReplies.Emby("wrong-token").Exchanges[0].Reply;
        var client = new EmbyMediaServerClient(Factory(_ =>
        {
            var response = refused.ToResponse();
            response.StatusCode = code;
            return response;
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Emby), "movies", CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Unreachable>(answer);
    }

    /// <summary>Plex 1.43.4 with its connection refused, as recorded (tests/fixtures/recorded/plex/not-answering).</summary>
    [Fact]
    public async Task A_server_that_is_down_could_not_be_asked()
    {
        var client = new PlexMediaServerClient(new HandlerFactory(RecordedReplies.Plex("not-answering").NotAnswering()));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), "movies", CancellationToken.None);

        var unreachable = Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Unreachable>(answer);
        Assert.Contains("could not reach", unreachable.Reason, StringComparison.Ordinal);
    }

    /// <summary>A second page that fails must fail the whole answer, not hand back the first page as if it were everything. The first page is the real 500 of 503.</summary>
    [Fact]
    public async Task A_listing_that_breaks_part_way_could_not_be_asked()
    {
        var calls = 0;
        var client = new JellyfinMediaServerClient(Factory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users") return RecordedReplies.Jellyfin("users-several").Exchanges[0].Reply.ToResponse();
            if (request.RequestUri.AbsolutePath != "/Items") return RecordedReplies.Jellyfin("users-several").Exchanges[2].Reply.ToResponse();
            if (calls++ > 0) return new HttpResponseMessage(HttpStatusCode.BadGateway);
            return RecordedReplies.Jellyfin("catalogue-paged").Exchanges[0].Reply.ToResponse();
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Jellyfin), "movies", CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Unreachable>(answer);
    }

    /// <summary>A library over one page of 500 is read whole: 503 films from the two recorded pages, on Jellyfin 12.4.0 and Emby 4.10.1.0.</summary>
    [Theory]
    [InlineData(MediaServerKinds.Jellyfin)]
    [InlineData(MediaServerKinds.Emby)]
    public async Task A_library_over_one_page_is_read_whole(string kind)
    {
        Recording Recorded(string state) => kind == MediaServerKinds.Emby ? RecordedReplies.Emby(state) : RecordedReplies.Jellyfin(state);
        var pages = Recorded("catalogue-paged");
        var starts = new List<string>();
        IHttpClientFactory factory = Factory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/Users", StringComparison.Ordinal)) return Recorded("users-several").Exchanges[0].Reply.ToResponse();
            if (!path.EndsWith("/Items", StringComparison.Ordinal) || path.StartsWith("/Users/", StringComparison.Ordinal) || path.StartsWith("/emby/Users/", StringComparison.Ordinal)) return Recorded("users-several").Exchanges[2].Reply.ToResponse();
            var start = Uri.UnescapeDataString(request.RequestUri.Query).Split('&').Single(part => part.StartsWith("StartIndex=", StringComparison.Ordinal));
            starts.Add(start);
            return pages.Exchanges[start == "StartIndex=0" ? 0 : 1].Reply.ToResponse();
        });
        EmbyFamilyMediaServerClient client = kind == MediaServerKinds.Emby ? new EmbyMediaServerClient(factory) : new JellyfinMediaServerClient(factory);

        var answer = await client.ListItemsAsync(Server(kind), "movies", CancellationToken.None);

        var library = Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value;
        Assert.Equal(["StartIndex=0", "StartIndex=500"], starts);
        Assert.Equal(503, library.Items.Count);
        Assert.Equal(503, library.Items.Select(item => item.ServerItemId).Distinct().Count());
    }

    /// <summary>Emby 4.10.1.0 answers under /emby, and its connection test reports the server name, id and the number of libraries it recorded.</summary>
    [Fact]
    public async Task Emby_is_asked_under_its_emby_path_and_says_who_it_is()
    {
        var paths = new List<string>();
        var client = new EmbyMediaServerClient(Factory(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return FromRecordings(MediaServerKinds.Emby, request) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var info = RecordedReplies.Emby("identity").Exchanges[1].Reply.Json();
        var libraries = RecordedReplies.Emby("libraries").Exchanges[0].Reply.Json().AsArray().Count;

        var result = await client.TestAsync(Server(MediaServerKinds.Emby, "http://192.168.1.51:8097"), CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(info["ServerName"]!.GetValue<string>(), result.ServerName);
        Assert.Equal(info["Id"]!.GetValue<string>(), result.ServerId);
        Assert.Equal(libraries, result.LibraryCount);
        Assert.Contains(info["ServerName"]!.GetValue<string>(), result.Message, StringComparison.Ordinal);
        Assert.All(paths, path => Assert.StartsWith("/emby/", path, StringComparison.Ordinal));
    }

    /// <summary>
    /// Jellyfin answers a film that is in a collection with the collection,
    /// unless told not to. On the rig a title in "Leaving soon" vanished from
    /// the listing and was read as unwatched (#521). Every listing says not to
    /// group, and a listing that groups anyway is not an answer.
    /// </summary>
    [Fact]
    public async Task Jellyfin_is_asked_not_to_group_films_into_collections_and_a_grouped_listing_is_not_an_answer()
    {
        var queries = new List<string>();
        var client = new JellyfinMediaServerClient(Factory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (path == "/Users") return RecordedReplies.Jellyfin("users-several").Exchanges[0].Reply.ToResponse();
            if (query.Contains("IncludeItemTypes=Movie", StringComparison.Ordinal)) queries.Add(query);
            // Jellyfin 12.4.0's listing when it does group: the collection stands in for its film (recorded with CollapseBoxSetItems=true).
            if (path == "/Items" && query.Contains("IncludeItemTypes=Movie", StringComparison.Ordinal))
                return RecordedReplies.Jellyfin("catalogue-grouped").Exchanges[0].Reply.ToResponse();
            return RecordedReplies.Jellyfin("users-several").Exchanges[2].Reply.ToResponse();
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Jellyfin), "movies", CancellationToken.None);

        var unreachable = Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Unreachable>(answer);
        Assert.Contains("Recorded collection", unreachable.Reason, StringComparison.Ordinal);
        Assert.NotEmpty(queries);
        Assert.All(queries, query => Assert.Contains("CollapseBoxSetItems=false", query, StringComparison.Ordinal));
    }


    // ------------------------------------------------------ telling the server about files, and asking whether it can see a folder

    private const string PlexFolder = "/media/Movies/Sintel (2010)";
    private const string PlexFile = PlexFolder + "/Sintel (2010) [WEB 1080p].mkv";

    /// <summary>
    /// Plex 1.43.4's recorded replies to the library list and the refresh and empty-trash calls (tests/fixtures/recorded/plex), noting each
    /// call. The refresh with no path, with the path of a folder in a library and with a path in none are the three recorded.
    /// </summary>
    private static HttpResponseMessage PlexRecorded(HttpRequestMessage request, List<(HttpMethod Method, string PathAndQuery)> sent)
    {
        sent.Add((request.Method, Uri.UnescapeDataString(request.RequestUri!.PathAndQuery)));
        var path = request.RequestUri.AbsolutePath;
        var query = request.RequestUri.Query;
        var refresh = RecordedReplies.Plex("refresh");
        if (path == "/library/sections") return RecordedReplies.Plex("sections").Single.Reply.ToResponse();
        if (path.EndsWith("/refresh", StringComparison.Ordinal)) return refresh.Exchanges[!query.Contains("path=", StringComparison.Ordinal) ? 0 : 1].Reply.ToResponse();
        if (path.EndsWith("/emptyTrash", StringComparison.Ordinal)) return refresh.Exchanges[3].Reply.ToResponse();
        throw new InvalidOperationException($"No Plex recording answers {request.Method} {path}{query}.");
    }

    [Fact]
    public async Task Removing_from_Plex_rescans_the_folder_and_empties_trash_and_never_deletes()
    {
        var sent = new List<(HttpMethod Method, string PathAndQuery)>();
        var client = new PlexMediaServerClient(Factory(request => PlexRecorded(request, sent)));
        var item = new MediaServerItem("1", "movies", "Sintel", 2010, null, null, null, [PlexFile], null, MediaServerWatchState.Unplayed, "1");

        var answer = await client.RemoveAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), item, CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerRemoval>.Reported>(answer);
        Assert.Equal((HttpMethod.Get, $"/library/sections/1/refresh?path={PlexFolder}"), sent[0]);
        Assert.Equal((HttpMethod.Put, "/library/sections/1/emptyTrash"), sent[1]);
        Assert.DoesNotContain(sent, call => call.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task A_Plex_refresh_rescans_the_folder_of_a_section_whose_location_matches_the_path()
    {
        var sent = new List<(HttpMethod Method, string PathAndQuery)>();
        var client = new PlexMediaServerClient(Factory(request => PlexRecorded(request, sent)));

        var answer = await client.RefreshAsync(
            Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"),
            "movies",
            [new MediaServerPathChange(PlexFile, MediaServerChangeKind.Created)],
            CancellationToken.None);

        var refresh = Assert.IsType<MediaServerAnswer<MediaServerRefresh>.Reported>(answer).Value;
        Assert.False(refresh.UsedFallbackScan);
        Assert.Contains((HttpMethod.Get, $"/library/sections/1/refresh?path={PlexFolder}"), sent);
        Assert.DoesNotContain(sent, call => call.PathAndQuery.StartsWith("/library/sections/2", StringComparison.Ordinal));
        Assert.DoesNotContain(sent, call => call.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task A_Plex_refresh_empties_trash_for_a_deleted_change_but_never_for_created()
    {
        var deleted = new List<(HttpMethod Method, string PathAndQuery)>();
        var created = new List<(HttpMethod Method, string PathAndQuery)>();
        var server = Server(MediaServerKinds.Plex, "http://192.168.1.51:32400");

        await new PlexMediaServerClient(Factory(request => PlexRecorded(request, deleted)))
            .RefreshAsync(server, "movies", [new MediaServerPathChange(PlexFile, MediaServerChangeKind.Deleted)], CancellationToken.None);
        await new PlexMediaServerClient(Factory(request => PlexRecorded(request, created)))
            .RefreshAsync(server, "movies", [new MediaServerPathChange(PlexFile, MediaServerChangeKind.Created)], CancellationToken.None);

        Assert.Contains((HttpMethod.Put, "/library/sections/1/emptyTrash"), deleted);
        Assert.DoesNotContain(created, call => call.PathAndQuery.Contains("emptyTrash", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Plex_refresh_falls_back_to_a_full_scan_when_no_section_maps_the_path()
    {
        var sent = new List<(HttpMethod Method, string PathAndQuery)>();
        var client = new PlexMediaServerClient(Factory(request => PlexRecorded(request, sent)));

        var answer = await client.RefreshAsync(
            Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"),
            "movies",
            [new MediaServerPathChange("/unmapped/Movies/Sintel (2010)/Sintel.mkv", MediaServerChangeKind.Created)],
            CancellationToken.None);

        var refresh = Assert.IsType<MediaServerAnswer<MediaServerRefresh>.Reported>(answer).Value;
        Assert.True(refresh.UsedFallbackScan);
        Assert.Contains((HttpMethod.Get, "/library/sections/1/refresh"), sent);
        Assert.DoesNotContain(sent, call => call.PathAndQuery.Contains("path=", StringComparison.Ordinal));
    }

    /// <summary>Jellyfin 12.4.0 told of a created and a deleted file: its recorded 204 answers the one Library/Media/Updated call that names both.</summary>
    [Fact]
    public async Task Jellyfin_and_Emby_refresh_sends_one_call_with_every_changed_path()
    {
        var calls = new List<(string Path, string? Body)>();
        var client = new JellyfinMediaServerClient(Factory(request =>
        {
            calls.Add((request.RequestUri!.AbsolutePath, request.Content?.ReadAsStringAsync().GetAwaiter().GetResult()));
            return RecordedReplies.Jellyfin("refresh").Exchanges[1].Reply.ToResponse();
        }));

        var answer = await client.RefreshAsync(
            Server(MediaServerKinds.Jellyfin),
            "tv",
            [
                new MediaServerPathChange("/tv/Recorded Show/Season 01/S01E01.mkv", MediaServerChangeKind.Created),
                new MediaServerPathChange("/tv/Recorded Show/Season 01/S01E01.old.mkv", MediaServerChangeKind.Deleted)
            ],
            CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerRefresh>.Reported>(answer);
        var call = Assert.Single(calls);
        Assert.Equal("/Library/Media/Updated", call.Path);
        Assert.Contains("\"Created\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"Deleted\"", call.Body, StringComparison.Ordinal);
    }

    /// <summary>Jellyfin 12.4.0's own 400 for a body it cannot read in place of the targeted call, and its recorded 204 for the full scan asked for instead.</summary>
    [Fact]
    public async Task Jellyfin_falls_back_to_a_full_scan_when_the_targeted_refresh_fails()
    {
        var calls = new List<string>();
        var client = new JellyfinMediaServerClient(Factory(request =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath == "/Library/Media/Updated"
                ? RecordedReplies.Jellyfin("media-updated-refused").Exchanges[0].Reply.ToResponse()
                : RecordedReplies.Jellyfin("refresh").Exchanges[0].Reply.ToResponse();
        }));

        var answer = await client.RefreshAsync(
            Server(MediaServerKinds.Jellyfin),
            "movies",
            [new MediaServerPathChange("/media/Movies/Sintel (2010)/Sintel.mkv", MediaServerChangeKind.Created)],
            CancellationToken.None);

        var refresh = Assert.IsType<MediaServerAnswer<MediaServerRefresh>.Reported>(answer).Value;
        Assert.True(refresh.UsedFallbackScan);
        Assert.Contains("/Library/Refresh", calls);
    }

    /// <summary>Jellyfin 12.4.0 told a removed film's file is gone: one Library/Media/Updated call naming the file Deleted, and never its own delete.</summary>
    [Fact]
    public async Task Removing_from_Jellyfin_reports_the_files_deleted_and_never_calls_its_delete()
    {
        var sent = new List<(HttpMethod Method, string Path, string? Body)>();
        var client = new JellyfinMediaServerClient(Factory(request =>
        {
            sent.Add((request.Method, request.RequestUri!.AbsolutePath, request.Content?.ReadAsStringAsync().GetAwaiter().GetResult()));
            return RecordedReplies.Jellyfin("refresh").Exchanges[1].Reply.ToResponse();
        }));
        var file = RecordedReplies.Jellyfin("catalogue-movies").Exchanges[0].Reply.Json()["Items"]![0]!["Path"]!.GetValue<string>();
        var item = new MediaServerItem("m1", "movies", "Sintel", 2010, null, null, null, [file], null, MediaServerWatchState.Unplayed);

        var answer = await client.RemoveAsync(Server(MediaServerKinds.Jellyfin), item, CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerRemoval>.Reported>(answer);
        var call = Assert.Single(sent);
        Assert.Equal((HttpMethod.Post, "/Library/Media/Updated"), (call.Method, call.Path));
        Assert.Contains("\"UpdateType\":\"Deleted\"", call.Body, StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(file), call.Body, StringComparison.Ordinal);
    }

    /// <summary>Jellyfin 12.4.0's recorded listing of /media/Movies is a folder it can see.</summary>
    [Fact]
    public async Task A_Jellyfin_path_mapping_test_reports_whether_the_server_can_see_the_folder()
    {
        var seen = RecordedReplies.Jellyfin("directory-contents").Exchanges[0].Reply;
        var client = new JellyfinMediaServerClient(Factory(request => request.RequestUri!.AbsolutePath == "/Environment/DirectoryContents" ? seen.ToResponse() : new HttpResponseMessage(HttpStatusCode.NotFound)));

        var answer = await client.TestPathAsync(Server(MediaServerKinds.Jellyfin), "/media/Movies", CancellationToken.None);

        Assert.True(Assert.IsType<MediaServerAnswer<MediaServerPathMappingTest>.Reported>(answer).Value.Found);
    }

    /// <summary>A Plex that answers the folder test with a refusal (a 401 in place of the recorded browse reply) could not be asked.</summary>
    [Fact]
    public async Task A_Plex_path_mapping_test_reports_the_server_could_not_be_asked()
    {
        var client = new PlexMediaServerClient(Factory(_ =>
        {
            var response = RecordedReplies.Plex("directory-contents").Exchanges[0].Reply.ToResponse();
            response.StatusCode = HttpStatusCode.Unauthorized;
            return response;
        }));

        var answer = await client.TestPathAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), "/media/Movies", CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerPathMappingTest>.Unreachable>(answer);
    }

    // ------------------------------------------------------ what is playing now (#1012)

    private static IReadOnlyList<MediaServerPlay> Plays(MediaServerAnswer<IReadOnlyList<MediaServerPlay>> answer)
        => Assert.IsType<MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported>(answer).Value;

    /// <summary>The recorded episode of the recorded show (Plex's own episode listing), as a play's item.</summary>
    private static JsonNode PlexEpisode()
        => RecordedReplies.Plex("catalogue-series").Exchanges[4].Reply.Json()["MediaContainer"]!["Metadata"]![0]!.DeepClone();

    /// <summary>
    /// Plex 1.43.4's recorded session with the film playing, plus a recorded episode of the recorded show as a second play (the server was
    /// recorded with a film playing only; the episode is the one thing added). The show it belongs to is answered by the recorded show
    /// page, whose own ids are what the play must carry.
    /// </summary>
    [Fact]
    public async Task Plex_names_the_ids_file_and_provider_ids_of_every_play()
    {
        var episode = PlexEpisode();
        var showKey = episode["grandparentRatingKey"]!.GetValue<string>();
        var showGuids = RecordedReplies.Plex("catalogue-series").Exchanges[3].Reply.Json()["MediaContainer"]!["Metadata"]![0]!["Guid"]!.AsArray()
            .Select(guid => guid!["id"]!.GetValue<string>()).ToList();
        string IdOf(string scheme) => showGuids.Single(guid => guid.StartsWith(scheme + "://", StringComparison.Ordinal))[(scheme.Length + 3)..];
        var sessions = RecordedReplies.Plex("sessions-playing").Exchanges[1].Reply.Json();
        sessions["MediaContainer"]!["Metadata"]!.AsArray().Add(PlexEpisode());
        var client = new PlexMediaServerClient(Factory(request => request.RequestUri!.AbsolutePath == "/status/sessions"
            ? RecordedReplies.Json(sessions.ToJsonString())
            : request.RequestUri.AbsolutePath == $"/library/metadata/{showKey}"
                ? RecordedReplies.Plex("catalogue-series").Exchanges[3].Reply.ToResponse()
                : throw new InvalidOperationException($"No Plex recording answers {request.RequestUri.PathAndQuery}.")));

        var plays = Plays(await client.ListPlayingAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), CancellationToken.None));

        var film = plays[0];
        Assert.True(film.IsMovie);
        Assert.Contains(film.Paths, path => path.EndsWith("Sintel (2010) [WEB 1080p].mkv", StringComparison.Ordinal));
        var play = plays[1];
        Assert.False(play.IsMovie);
        Assert.Equal([episode["ratingKey"]!.GetValue<string>(), episode["parentRatingKey"]!.GetValue<string>(), showKey], play.ItemIds);
        Assert.Equal((true, episode["grandparentTitle"]!.GetValue<string>(), IdOf("tmdb"), IdOf("tvdb"), IdOf("imdb")), (play.IsEpisode, play.ShowName, play.ShowTmdbId, play.ShowTvdbId, play.ShowImdbId));
        Assert.Equal((episode["parentIndex"]!.GetValue<int>(), episode["index"]!.GetValue<int>()), (play.SeasonNumber, play.EpisodeNumber));
    }

    /// <summary>The same episode play when the show page cannot be read (a 500 in place of the recorded show page): the show's ids stay empty, so it reads as cannot tell.</summary>
    [Fact]
    public async Task Plex_leaves_the_shows_ids_empty_when_the_show_cannot_be_read_so_it_reads_as_cannot_tell()
    {
        var sessions = RecordedReplies.Plex("sessions-playing").Exchanges[1].Reply.Json();
        var plays = sessions["MediaContainer"]!["Metadata"]!.AsArray();
        plays.Clear();
        plays.Add(PlexEpisode());
        var client = new PlexMediaServerClient(Factory(request => request.RequestUri!.AbsolutePath == "/status/sessions"
            ? RecordedReplies.Json(sessions.ToJsonString())
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var play = Assert.Single(Plays(await client.ListPlayingAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), CancellationToken.None)));

        Assert.True(play.IsEpisode);
        Assert.Null(play.ShowTmdbId);
        Assert.Null(play.ShowTvdbId);
        Assert.Null(play.ShowImdbId);
    }

    /// <summary>Plex's recorded idle session list is nobody playing; a refusal, and a reply that is not a session list (the recorded one with its MediaContainer taken out), are "could not ask".</summary>
    [Fact]
    public async Task Plex_with_nothing_playing_says_nothing_is_playing_but_an_unreadable_answer_says_it_could_not_ask()
    {
        var idle = RecordedReplies.Plex("sessions-idle").Single.Reply;
        var quiet = new PlexMediaServerClient(Factory(_ => idle.ToResponse()));
        Assert.Empty(Plays(await quiet.ListPlayingAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), CancellationToken.None)));

        var refused = new PlexMediaServerClient(Factory(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        Assert.IsType<MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Unreachable>(await refused.ListPlayingAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), CancellationToken.None));

        var nonsense = new PlexMediaServerClient(Factory(_ => RecordedReplies.Json(idle.BodyWith(("MediaContainer", null)))));
        Assert.IsType<MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Unreachable>(await nonsense.ListPlayingAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), CancellationToken.None));
    }

    /// <summary>
    /// What Jellyfin 12.4.0 and Emby 4.10.1.0 list as playing: their recorded sessions (the film being played and a session with nothing in
    /// it), with a recorded episode of the recorded show put in as the idle session's item (the servers were recorded with a film playing
    /// only). Its show is answered by the recorded show lookup with its own ids set (the recording holds none).
    /// </summary>
    [Theory]
    [InlineData(MediaServerKinds.Jellyfin, "/Sessions")]
    [InlineData(MediaServerKinds.Emby, "/emby/Sessions")]
    public async Task Jellyfin_and_Emby_read_the_sessions_that_have_something_playing(string kind, string expectedPath)
    {
        var episode = Recorded(kind, "catalogue-series").Exchanges[0].Reply.Json()["Items"]!.AsArray().First(item => item!["Type"]!.GetValue<string>() == "Episode")!;
        var showId = episode["SeriesId"]!.GetValue<string>();
        var sessions = Recorded(kind, "sessions-playing").Exchanges[2].Reply.Json().AsArray();
        sessions.Single(session => session!["NowPlayingItem"] is null)!["NowPlayingItem"] = episode.DeepClone();
        var show = Recorded(kind, "catalogue-series").Exchanges[1].Reply.BodyWith(("Items.0.ProviderIds", new JsonObject { ["Tvdb"] = "81189", ["Tmdb"] = "1396", ["Imdb"] = "tt0903747" }));
        IHttpClientFactory factory = Factory(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/Items", StringComparison.Ordinal))
            {
                Assert.Contains($"Ids={showId}", request.RequestUri.Query, StringComparison.Ordinal);
                return RecordedReplies.Json(show);
            }

            Assert.Equal(expectedPath, request.RequestUri!.AbsolutePath);
            return RecordedReplies.Json(sessions.ToJsonString());
        });
        var client = (IMediaServerPlaybackReader)ClientOf(kind, factory);

        var plays = Plays(await client.ListPlayingAsync(Server(kind), CancellationToken.None));

        Assert.Equal(2, plays.Count);
        Assert.NotEmpty(plays.Single(play => play.IsMovie).Paths);
        var played = plays.Single(play => !play.IsMovie);
        Assert.Equal([episode["Id"]!.GetValue<string>(), episode["SeasonId"]!.GetValue<string>(), showId], played.ItemIds);
        Assert.Equal((true, episode["SeriesName"]!.GetValue<string>(), "1396", "81189", "tt0903747", 1, 1), (played.IsEpisode, played.ShowName, played.ShowTmdbId, played.ShowTvdbId, played.ShowImdbId, played.SeasonNumber, played.EpisodeNumber));
        Assert.Equal([episode["Path"]!.GetValue<string>()], played.Paths);
    }

    private static IHttpClientFactory Factory(Func<HttpRequestMessage, HttpResponseMessage> send)
        => new StubFactory(new HttpClient(new StubHandler(send)));

    private sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
