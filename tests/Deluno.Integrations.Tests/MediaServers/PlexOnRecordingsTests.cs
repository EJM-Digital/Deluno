using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Connections.Versions;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.MediaServers;

/// <summary>
/// Deluno's Plex client against Plex Media Server 1.43.4's own replies, recorded from the real server in Docker with a library of one film
/// and a two-episode series (tests/fixtures/recorded/plex, #1237). The server is UNCLAIMED (no Plex account was made), so every call is local
/// and carries no token, and what needs plex.tv (the user list, shared servers, resources) is not covered here (tests/fixtures/recorded/MISSING.md).
/// A call is answered by the recorded reply to the same kind of call; the ids in a call are the test's own.
/// </summary>
public sealed class PlexOnRecordingsTests
{
    private const string Folder = "/media/Movies/Sintel (2010)";
    private const string File = Folder + "/Sintel (2010) [WEB 1080p].mkv";

    private static MediaServerConnection Server(string? token = null)
        => new("server-1", MediaServerKinds.Plex, "Plex", "http://192.168.1.51:32400", token, true, "untested", null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>A Plex answering from its recordings, noting every call made to it (method, address, headers kept).</summary>
    private sealed class Plex(Func<HttpRequestMessage, HttpResponseMessage?>? answer = null)
    {
        public List<string> Calls { get; } = [];

        public PlexMediaServerClient Client() => new(new Factory(RecordedReplies.Handler("plex", [], (request, _) =>
        {
            Calls.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            return answer?.Invoke(request) ?? RecordedReply(request);
        })));

        public bool Sent(string fragment) => Calls.Any(call => call.Contains(fragment, StringComparison.Ordinal));
    }

    private static Recording R(string state) => RecordedReplies.Plex(state);

    private static HttpResponseMessage Reply(string state, int exchange) => R(state).Exchanges[exchange].Reply.ToResponse();

    /// <summary>The recorded reply to the kind of call Deluno made, found by address.</summary>
    private static HttpResponseMessage RecordedReply(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;
        HttpResponseMessage? reply = null;
        // plex.tv, which an unclaimed server's questions about the account go to: it answers each with its own 401 (no token was sent).
        if (request.RequestUri.Host == "discover.provider.plex.tv") return Reply("plex-tv-no-token", 5);
        if (request.RequestUri.Host == "plex.tv")
        {
            return path == "/api/v2/user" ? Reply("plex-tv-no-token", 0)
                : path == "/api/v2/resources" ? Reply("plex-tv-no-token", 1)
                : path.EndsWith("/shared_servers", StringComparison.Ordinal) ? Reply("plex-tv-no-token", 2)
                : path == "/api/users" ? Reply("plex-tv-no-token", 3)
                : Reply("plex-tv-no-token", 4);
        }

        if (path == "/") reply = Reply("root", 0);
        else if (path == "/identity") reply = Reply("identity", 0);
        else if (path == "/library/sections") reply = Reply("sections", 0);
        else if (path == "/myplex/account") reply = Reply("accounts", 1);
        else if (path == "/accounts") reply = Reply("accounts", 0);
        else if (path == "/library/sections/1/all" && query.Contains("type=1", StringComparison.Ordinal)) reply = Reply("catalogue-movies", 0);
        else if (path == "/library/sections/2/all" && query.Contains("type=2", StringComparison.Ordinal)) reply = Reply("catalogue-series", 0);
        else if (path == "/library/sections/2/all" && query.Contains("type=3", StringComparison.Ordinal)) reply = Reply("catalogue-series", 1);
        else if (path == "/library/sections/2/all" && query.Contains("type=4", StringComparison.Ordinal)) reply = Reply("catalogue-series", 2);
        else if (path == "/library/sections/1/collections") reply = Reply("collections-empty", 0);
        else if (path == "/library/sections/2/collections") reply = Reply("collections-empty", 1);
        else if (path == "/library/metadata/1") reply = Reply("catalogue-movies", 1);
        else if (path == "/library/metadata/2") reply = Reply("catalogue-series", 3);
        else if (path == "/library/metadata/2/allLeaves") reply = Reply("catalogue-series", 4);
        else if (path == "/status/sessions") reply = Reply("sessions-playing", 1);
        else if (path == "/playlists") reply = Reply("playlists", 0);
        else if (path.StartsWith("/library/sections/", StringComparison.Ordinal) && path.EndsWith("/refresh", StringComparison.Ordinal))
            reply = Reply("refresh", !query.Contains("path=", StringComparison.Ordinal) ? 0 : query.Contains("nowhere", StringComparison.Ordinal) ? 2 : 1);
        else if (path.EndsWith("/emptyTrash", StringComparison.Ordinal)) reply = Reply("refresh", 3);
        return reply ?? throw new InvalidOperationException($"No Plex recording answers {request.Method} {path}{query}.");
    }

    // ---- who the server is ----

    [Fact]
    public void The_identity_a_fresh_server_gives_before_anything_is_set_up_carries_its_version_and_is_not_claimed()
    {
        var identity = R("identity").Single.Reply;

        Assert.Equal("1.43.4.10903-e5521bd8c", ConnectionVersionParsers.FromPlexIdentity(identity.Body));
        var container = identity.Json()["MediaContainer"]!;
        Assert.False((bool)container["claimed"]!);
        Assert.Equal("startingPlugins", (string?)container["startState"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)container["machineIdentifier"]));
    }

    [Fact]
    public async Task The_connection_test_reads_the_server_root_and_its_two_libraries_and_gives_the_version()
    {
        var plex = new Plex();

        var result = await plex.Client().TestAsync(Server(), CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(2, result.LibraryCount);
        Assert.Equal("1.43.4.10903-e5521bd8c", result.Version);
        Assert.False(string.IsNullOrWhiteSpace(result.ServerId));
        Assert.Equal(["GET /", "GET /library/sections"], plex.Calls);
    }

    [Fact]
    public async Task An_unclaimed_server_answers_any_token_the_same_way_so_a_wrong_one_is_not_refused_and_the_token_is_still_sent()
    {
        // The recorded library list asked with a token Plex does not know: 200, as with no token. Only a claimed server refuses.
        Assert.Equal(200, R("wrong-token").Single.Reply.Status);
        string? sent = null;
        var plex = new Plex(request =>
        {
            sent ??= request.Headers.TryGetValues("X-Plex-Token", out var values) ? values.Single() : null;
            return null;
        });

        var result = await plex.Client().TestAsync(Server("not-a-token"), CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("not-a-token", sent);
    }

    // ---- the catalogue ----

    [Fact]
    public async Task The_movie_library_has_the_film_with_its_ids_and_the_file_Plex_holds()
    {
        var answer = await new Plex().Client().ListItemsAsync(Server(), "movies", CancellationToken.None);

        var library = Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value;
        var film = Assert.Single(library.Items);
        Assert.Equal("Sintel", film.Title);
        Assert.Equal(2010, film.Year);
        Assert.Equal("45745", film.TmdbId);
        Assert.Contains(film.Paths, path => path.EndsWith("Sintel (2010) [WEB 1080p].mkv", StringComparison.Ordinal));
        Assert.Equal("1", film.SectionId);
        // plex.tv answered 401 to the watchlist question (no token), so the watchlist is unknown, not empty.
        Assert.Null(film.WatchlistedBy);
        Assert.Equal(401, R("plex-tv-no-token").Exchanges[5].Reply.Status);
    }

    [Fact]
    public async Task The_series_library_holds_the_show_and_its_episodes()
    {
        var answer = await new Plex().Client().ListItemsAsync(Server(), "tv", CancellationToken.None);

        var library = Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value;
        // The agent named the show by what TheTVDB and TMDb matched the folder to, which is what Deluno reads (not the folder's own name).
        var show = Assert.Single(library.Items, item => item.Level == MediaServerItemLevels.Show);
        Assert.False(string.IsNullOrWhiteSpace(show.TmdbId));
        Assert.Single(library.Items, item => item.Level == MediaServerItemLevels.Season);
        Assert.Equal(2, library.Items.Count(item => item.Level == MediaServerItemLevels.Episode));
        Assert.All(library.Items.Where(item => item.Level == MediaServerItemLevels.Episode), episode => Assert.Equal(show.ServerItemId, episode.ShowServerItemId));
    }

    [Fact]
    public async Task A_film_marked_watched_on_the_server_is_read_as_watched_by_the_account_and_a_fresh_one_as_unwatched()
    {
        var unwatched = await new Plex().Client().ListItemsAsync(Server(), "movies", CancellationToken.None);
        var watched = await new Plex(request => request.RequestUri!.AbsolutePath switch
        {
            "/library/metadata/1" => Reply("watched", 1),
            "/library/sections/1/all" => Reply("watched", 2),
            _ => null
        }).Client().ListItemsAsync(Server(), "movies", CancellationToken.None);

        Assert.False(Assert.Single(Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(unwatched).Value.Items).Watch.PlayedByAnyone);
        Assert.True(Assert.Single(Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(watched).Value.Items).Watch.PlayedByAnyone);
        Assert.Contains("viewCount", R("watched").Exchanges[1].Reply.Body, StringComparison.Ordinal);
        Assert.Equal(200, R("watched").Exchanges[0].Reply.Status);
    }

    [Fact]
    public void The_account_lists_hold_the_one_administrator_and_the_myplex_state_says_nobody_signed_in()
    {
        var accounts = R("accounts");

        Assert.Equal("Administrator", (string?)accounts.Exchanges[0].Reply.Json()["MediaContainer"]!["Account"]![0]!["name"]);
        Assert.Equal("none", (string?)accounts.Exchanges[1].Reply.Json()["MyPlex"]!["signInState"]);
    }

    [Fact]
    public void There_are_no_video_playlists_on_a_fresh_server_and_the_server_says_it_was_still_starting_when_asked_for_a_library_too_early()
    {
        Assert.Equal(0, (int)R("playlists").Single.Reply.Json()["MediaContainer"]!["size"]!);
        var early = R("starting-up").Single.Reply;
        Assert.Equal(400, early.Status);
        Assert.Contains("still starting up", early.Body, StringComparison.Ordinal);
        Assert.Equal(201, R("libraries-created").Exchanges[1].Reply.Status);
        Assert.Equal(201, R("libraries-created").Exchanges[2].Reply.Status);
        Assert.Equal(0, (int)R("libraries-created").Exchanges[0].Reply.Json()["MediaContainer"]!["size"]!);
        Assert.Equal(2, (int)R("libraries-created").Exchanges[3].Reply.Json()["MediaContainer"]!["size"]!);
    }

    // ---- what is playing ----

    [Fact]
    public async Task A_film_being_played_is_listed_with_its_ids()
    {
        var answer = await new Plex().Client().ListPlayingAsync(Server(), CancellationToken.None);

        var plays = Assert.IsType<MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported>(answer).Value;
        var play = Assert.Single(plays);
        Assert.True(play.IsMovie);
        // The real /status/sessions carries Plex's own guid for the film but no Guid list (no TMDb or IMDb id), so the file is what names it.
        Assert.Null(play.TmdbId);
        Assert.Contains(play.Paths, path => path.EndsWith("Sintel (2010) [WEB 1080p].mkv", StringComparison.Ordinal));
        Assert.Equal(200, R("sessions-playing").Exchanges[0].Reply.Status);
    }

    [Fact]
    public async Task With_no_one_playing_nothing_is_listed()
    {
        var idle = R("sessions-idle").Single.Reply;

        var answer = await new Plex(_ => idle.ToResponse()).Client().ListPlayingAsync(Server(), CancellationToken.None);

        Assert.Empty(Assert.IsType<MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported>(answer).Value);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_could_not_be_asked_and_is_never_read_as_nobody_playing()
    {
        var down = R("not-answering").NotAnswering();
        var client = new PlexMediaServerClient(new Factory(down));

        Assert.IsType<MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Unreachable>(await client.ListPlayingAsync(Server(), CancellationToken.None));
        Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Unreachable>(await client.ListItemsAsync(Server(), "movies", CancellationToken.None));
        Assert.False((await client.TestAsync(Server(), CancellationToken.None)).Ok);
    }

    // ---- refresh and removal ----

    [Fact]
    public async Task A_changed_folder_inside_a_library_is_refreshed_by_its_path_and_a_deleted_file_empties_the_trash()
    {
        var plex = new Plex();

        var answer = await plex.Client().RefreshAsync(Server(), "movies", [new MediaServerPathChange(File, MediaServerChangeKind.Deleted)], CancellationToken.None);

        var refresh = Assert.IsType<MediaServerAnswer<MediaServerRefresh>.Reported>(answer).Value;
        Assert.False(refresh.UsedFallbackScan);
        Assert.True(plex.Sent($"GET /library/sections/1/refresh?path={Uri.EscapeDataString(Folder)}"), string.Join(" | ", plex.Calls));
        Assert.True(plex.Sent("PUT /library/sections/1/emptyTrash"));
    }

    [Fact]
    public async Task A_changed_folder_no_library_holds_falls_back_to_a_full_rescan_of_the_libraries_of_that_kind()
    {
        var plex = new Plex();

        var answer = await plex.Client().RefreshAsync(Server(), "movies", [new MediaServerPathChange("/nowhere/Not A Film (1999)/Not A Film (1999).mkv", MediaServerChangeKind.Created)], CancellationToken.None);

        Assert.True(Assert.IsType<MediaServerAnswer<MediaServerRefresh>.Reported>(answer).Value.UsedFallbackScan);
        Assert.True(plex.Sent("GET /library/sections/1/refresh"));
        Assert.DoesNotContain(plex.Calls, call => call.Contains("path=", StringComparison.Ordinal));
        Assert.Equal(200, R("refresh").Exchanges[2].Reply.Status);
    }

    [Fact]
    public async Task Removing_an_item_rescans_the_folder_it_was_in_and_empties_the_trash_of_its_library()
    {
        var plex = new Plex();
        var item = new MediaServerItem("1", "movies", "Sintel", 2010, "45745", "tt1727587", null, [File], null, MediaServerWatchState.Unplayed) { SectionId = "1" };

        var answer = await plex.Client().RemoveAsync(Server(), item, CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerRemoval>.Reported>(answer);
        Assert.True(plex.Sent($"GET /library/sections/1/refresh?path={Uri.EscapeDataString(Folder)}"), string.Join(" | ", plex.Calls));
        Assert.True(plex.Sent("PUT /library/sections/1/emptyTrash"));
    }

    // ---- the folder browser ----

    [Fact]
    public async Task A_folder_Plex_can_see_is_found_by_its_parents_listing_and_a_folder_it_cannot_is_not()
    {
        var plex = new Plex(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (!path.StartsWith("/services/browse/", StringComparison.Ordinal))
            {
                return null;
            }

            // The listing of the folder asked for, by the base64 of its path (what Plex's own browser uses).
            var asked = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(path["/services/browse/".Length..])));
            return asked switch
            {
                "/media/Movies" => Reply("directory-contents", 0),
                "/" => Reply("directory-contents", 1),
                _ => Reply("directory-contents", 2)
            };
        });

        var visible = await plex.Client().TestPathAsync(Server(), Folder, CancellationToken.None);
        var invisible = await plex.Client().TestPathAsync(Server(), "/nowhere", CancellationToken.None);

        Assert.True(Assert.IsType<MediaServerAnswer<MediaServerPathMappingTest>.Reported>(visible).Value.Found);
        Assert.False(Assert.IsType<MediaServerAnswer<MediaServerPathMappingTest>.Reported>(invisible).Value.Found);
        Assert.DoesNotContain(plex.Calls, call => call.Contains("browse?path=", StringComparison.Ordinal));
    }

    [Fact]
    public void Plex_ignores_a_path_query_on_its_browser_and_lists_its_own_roots_for_any_path()
    {
        var old = R("directory-contents").Exchanges[3];

        Assert.Contains("path=%2Fnowhere", old.Request.Path, StringComparison.Ordinal);
        Assert.Equal(200, old.Reply.Status);
        Assert.True((int)old.Reply.Json()["MediaContainer"]!["size"]! > 1, "the roots of the server, whatever path was asked");
        Assert.Equal(0, (int)R("directory-contents").Exchanges[2].Reply.Json()["MediaContainer"]!["size"]!);
    }

    // ---- collections ----

    private static MediaServerCollectionRequest Request(string description, bool home, params string[] members)
        => new("movies", "Recorded collection", description, members.Select(id => new MediaServerCollectionMember(id, MediaServerItemLevels.Movie, "1")).ToArray(), home);

    /// <summary>The recorded replies of the collection calls, picked by address and method; a listing with no collection is the recorded empty container.</summary>
    private static HttpResponseMessage? CollectionReply(HttpRequestMessage request, bool exists)
    {
        var path = request.RequestUri!.AbsolutePath;
        var recording = R("collection-sync");
        if (path == "/library/sections/1/collections") return exists ? recording.Exchanges[0].Reply.ToResponse() : Reply("collections-empty", 0);
        if (path == "/library/collections" && request.Method == HttpMethod.Post) return recording.Exchanges[1].Reply.ToResponse();
        if (path == "/library/sections/1/all" && request.Method == HttpMethod.Put) return recording.Exchanges[2].Reply.ToResponse();
        if (path.EndsWith("/children", StringComparison.Ordinal)) return recording.Exchanges[3].Reply.ToResponse();
        if (path.StartsWith("/hubs/sections/", StringComparison.Ordinal)) return recording.Exchanges[request.Method == HttpMethod.Post ? 5 : 4].Reply.ToResponse();
        if (path.StartsWith("/library/collections/", StringComparison.Ordinal) && request.Method == HttpMethod.Delete) return R("collection-deleted").Exchanges[0].Reply.ToResponse();
        return null;
    }

    [Fact]
    public async Task A_new_collection_is_made_with_the_film_and_described_and_the_unclaimed_servers_404_for_its_home_screen_state_stops_the_sync()
    {
        // Plex 1.43.4, unclaimed: /hubs/sections/{id}/manage answers 404, so Deluno cannot read where the collection is promoted. (Whether a claimed
        // server answers it is not known: tests/fixtures/recorded/MISSING.md.) The collection itself was made and described before that call.
        Assert.Equal(404, R("collection-sync").Exchanges[4].Reply.Status);
        var plex = new Plex(request => CollectionReply(request, exists: false) ?? (request.RequestUri!.AbsolutePath == "/" ? Reply("root", 0) : null));

        var answer = await plex.Client().SyncCollectionAsync(Server(), Request("Leaving soon: these go on Friday.", home: false, "1"), CancellationToken.None);

        var unreachable = Assert.IsType<MediaServerAnswer<MediaServerCollectionSync>.Unreachable>(answer);
        Assert.Contains("404", unreachable.Reason, StringComparison.Ordinal);
        Assert.True(plex.Sent("POST /library/collections?type=1&title=Recorded%20collection&smart=0&sectionId=1&uri="), string.Join(" | ", plex.Calls));
        Assert.True(plex.Sent("PUT /library/sections/1/all?type=18&id=6&summary.value="));
        Assert.True(plex.Sent("GET /hubs/sections/1/manage?metadataItemId=6"));
        Assert.Equal(1, (int)R("collection-sync").Exchanges[3].Reply.Json()["MediaContainer"]!["size"]!);
    }

    [Fact]
    public async Task A_collection_that_is_to_go_on_the_home_screen_asks_for_its_promotion_and_the_404_is_said_as_unreachable()
    {
        Assert.Equal(404, R("collection-sync").Exchanges[5].Reply.Status);
        var plex = new Plex(request => CollectionReply(request, exists: true) ?? (request.RequestUri!.AbsolutePath == "/" ? Reply("root", 0) : null));

        var answer = await plex.Client().SyncCollectionAsync(Server(), Request("Leaving soon", home: true, "1"), CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerCollectionSync>.Unreachable>(answer);
        Assert.True(plex.Sent("/hubs/sections/1/manage"), string.Join(" | ", plex.Calls));
    }

    [Fact]
    public async Task A_collection_with_nothing_left_in_it_is_deleted_and_a_second_delete_is_the_servers_404()
    {
        var deleted = R("collection-deleted");
        Assert.Equal(200, deleted.Exchanges[0].Reply.Status);
        Assert.Equal(404, deleted.Exchanges[1].Reply.Status);
        var plex = new Plex(request => CollectionReply(request, exists: true) ?? (request.RequestUri!.AbsolutePath == "/" ? Reply("root", 0) : null));

        var answer = await plex.Client().SyncCollectionAsync(Server(), Request("Leaving soon", home: false), CancellationToken.None);

        Assert.True(Assert.IsType<MediaServerAnswer<MediaServerCollectionSync>.Reported>(answer).Value.Deleted);
        Assert.True(plex.Sent("DELETE /library/collections/6"));
    }

}
