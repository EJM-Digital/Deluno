using System.Net;
using System.Text.Json.Nodes;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.MediaServers;

/// <summary>
/// Deluno's Emby client against Emby Server 4.10.1.0's own replies, recorded from the real server in Docker with a library of one film and a
/// two-episode series (tests/fixtures/recorded/emby, #1237). Emby answers under <c>/emby</c> and takes its key in <c>X-Emby-Token</c>. A call
/// is answered by the recorded reply to the same kind of call; the ids in a call are the test's own.
/// </summary>
public sealed class EmbyOnRecordingsTests
{
    /// <summary>The user and the collection ids of the recording (what Emby made in that run).</summary>
    private static string UserId => (string)R("libraries").Exchanges[1].Reply.Json()[0]!["Id"]!;

    private static string CollectionId => (string)R("collection-sync").Exchanges[0].Reply.Json()["Id"]!;

    private static MediaServerConnection Server(string? token = "key")
        => new("server-1", MediaServerKinds.Emby, "Emby", "http://192.168.1.51:8096", token, true, "untested", null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>An Emby answering from its recordings, noting every call (method, address, body).</summary>
    private sealed class Emby(Func<HttpRequestMessage, string, HttpResponseMessage?>? answer = null)
    {
        public List<string> Calls { get; } = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        public EmbyMediaServerClient Client() => new(new Factory(RecordedReplies.Handler("emby", [], (request, body) =>
        {
            Calls.Add($"{request.Method} {request.RequestUri!.PathAndQuery} {body}".TrimEnd());
            Requests.Add(request);
            return answer?.Invoke(request, body) ?? RecordedReply(request);
        })));

        public bool Sent(string fragment) => Calls.Any(call => call.Contains(fragment, StringComparison.Ordinal));
    }

    private static Recording R(string state) => RecordedReplies.Emby(state);

    private static HttpResponseMessage Reply(string state, int exchange) => R(state).Exchanges[exchange].Reply.ToResponse();

    private static string UnderEmby(HttpRequestMessage request) => request.RequestUri!.AbsolutePath["/emby".Length..];

    /// <summary>The recorded reply to the kind of call: the catalogue calls pick the movie or the series reply by the item types asked for.</summary>
    private static HttpResponseMessage RecordedReply(HttpRequestMessage request)
    {
        Assert.StartsWith("/emby/", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        var path = UnderEmby(request);
        var query = request.RequestUri.Query;
        HttpResponseMessage? reply = null;
        if (path == "/System/Info") reply = Reply("identity", 1);
        else if (path == "/Library/VirtualFolders") reply = Reply("libraries", 0);
        else if (path == "/Users") reply = Reply("libraries", 1);
        else if (path == "/Sessions") reply = Reply("sessions-playing", 2);
        else if (path == "/Items" && query.Contains("IncludeItemTypes=Movie", StringComparison.Ordinal)) reply = Reply("catalogue-movies", 0);
        else if (path == "/Items" && query.Contains("IncludeItemTypes=Series", StringComparison.Ordinal)) reply = Reply("catalogue-series", 0);
        else if (path.StartsWith("/Users/", StringComparison.Ordinal) && path.EndsWith("/Items", StringComparison.Ordinal))
        {
            if (query.Contains("IncludeItemTypes=BoxSet", StringComparison.Ordinal)) reply = Reply("collection-sync", 6);
            else if (query.Contains("IncludeItemTypes=Playlist", StringComparison.Ordinal)) reply = Reply("collection-sync", 8);
            else if (query.Contains("ParentId", StringComparison.Ordinal)) reply = Reply("collection-sync", 7);
            else if (query.Contains("IsPlayed=true", StringComparison.Ordinal)) reply = Reply("watched", 1);
            else if (query.Contains("IsFavorite=true", StringComparison.Ordinal)) reply = Reply("watched", 2);
            else if (query.Contains("Filters=IsResumable", StringComparison.Ordinal)) reply = Reply("watched", 3);
            else reply = Reply("catalogue-series", 0);
        }
        else if (path == "/Library/Media/Updated") reply = Reply("refresh", 1);
        return reply ?? throw new InvalidOperationException($"No Emby recording answers {request.Method} {path}{query}.");
    }

    // ---- who the server is, and the key ----

    [Fact]
    public async Task The_connection_test_reads_the_server_info_libraries_and_users_and_sends_the_key_as_X_Emby_Token_under_emby()
    {
        var emby = new Emby();

        var result = await emby.Client().TestAsync(Server("the-key"), CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("4.10.1.0", result.Version);
        Assert.Equal(2, result.LibraryCount);
        Assert.NotEmpty(emby.Requests);
        Assert.All(emby.Requests, request =>
        {
            Assert.Equal("the-key", Assert.Single(request.Headers.GetValues("X-Emby-Token")));
            Assert.StartsWith("/emby/", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            Assert.False(request.Headers.Contains("Authorization"));
        });
    }

    [Fact]
    public async Task A_key_Emby_does_not_know_is_a_refused_token_on_the_test_and_a_listing_answers_unreachable_never_an_empty_library()
    {
        var refused = R("wrong-token").Exchanges[0].Reply;
        Assert.Equal(401, refused.Status);
        var emby = new Emby((_, _) => refused.ToResponse());

        var test = await emby.Client().TestAsync(Server("wrong"), CancellationToken.None);
        var listing = await emby.Client().ListItemsAsync(Server("wrong"), "movies", CancellationToken.None);

        Assert.False(test.Ok);
        Assert.Contains("refused the token (HTTP 401)", test.Message, StringComparison.Ordinal);
        Assert.Contains("refused the token", Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Unreachable>(listing).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_with_no_key_at_all_is_refused_the_same_way()
    {
        var none = R("no-token").Single.Reply;
        Assert.Equal(401, none.Status);

        var result = await new Emby((_, _) => none.ToResponse()).Client().TestAsync(Server(token: null), CancellationToken.None);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task A_key_that_can_read_the_server_but_not_list_its_users_is_degraded_and_the_version_is_still_read()
    {
        var noUsers = R("wrong-token").Exchanges[1].Reply;
        var emby = new Emby((request, _) => UnderEmby(request) == "/Users" ? noUsers.ToResponse() : null);

        var result = await emby.Client().TestAsync(Server(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("degraded", result.Status);
        Assert.Equal("4.10.1.0", result.Version);
    }

    [Fact]
    public void The_public_identity_carries_the_version_and_the_setup_wizard_is_the_series_of_calls_Emby_took_in_the_order_recorded()
    {
        Assert.Equal("4.10.1.0", (string?)R("identity").Exchanges[0].Reply.Json()["Version"]);
        var wizard = R("wizard").Exchanges;
        Assert.Null(wizard[0].Reply.Json()["StartupWizardCompleted"]);
        Assert.Equal(["GET", "GET", "GET", "POST", "GET", "POST", "POST", "POST"], wizard.Take(8).Select(e => e.Request.Method));
        Assert.Equal(204, wizard[7].Reply.Status);
        Assert.Equal(200, wizard[8].Reply.Status);
        Assert.Equal(204, wizard[9].Reply.Status);
        Assert.Equal("Deluno", (string?)wizard[10].Reply.Json()["Items"]![0]!["AppName"]);
    }

    // ---- the libraries and the catalogue ----

    [Fact]
    public async Task Before_any_library_exists_the_listing_is_reported_and_empty_not_unreachable()
    {
        var empty = R("library-empty");
        Assert.Equal(0, (int)empty.Exchanges[2].Reply.Json()["TotalRecordCount"]!);
        var emby = new Emby((request, _) => UnderEmby(request) == "/Items" ? empty.Exchanges[2].Reply.ToResponse() : UnderEmby(request) == "/Users" ? empty.Exchanges[1].Reply.ToResponse() : null);

        var answer = await emby.Client().ListItemsAsync(Server(), "movies", CancellationToken.None);

        Assert.Empty(Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value.Items);
    }

    [Fact]
    public void Two_libraries_made_over_the_folders_are_answered_204_and_listed_with_their_locations()
    {
        var created = R("libraries-created");

        Assert.Equal(204, created.Exchanges[0].Reply.Status);
        Assert.Equal(204, created.Exchanges[1].Reply.Status);
        var folders = created.Exchanges[2].Reply.Json().AsArray();
        Assert.Equal(["Movies", "Shows"], folders.Select(folder => (string?)folder!["Name"]).Order(StringComparer.Ordinal));
        Assert.Contains("/media/Movies", folders.SelectMany(folder => folder!["Locations"]!.AsArray()).Select(location => (string?)location));
    }

    [Fact]
    public async Task The_movie_catalogue_has_the_film_with_its_file_and_the_ids_Emby_holds()
    {
        var answer = await new Emby().Client().ListItemsAsync(Server(), "movies", CancellationToken.None);

        var film = Assert.Single(Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value.Items);
        Assert.Equal("Sintel", film.Title);
        Assert.Contains(film.Paths, path => path.EndsWith("Sintel (2010) [WEB 1080p].mkv", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(film.ServerItemId));
    }

    [Fact]
    public async Task The_series_catalogue_holds_the_show_its_season_and_episodes()
    {
        var answer = await new Emby().Client().ListItemsAsync(Server(), "tv", CancellationToken.None);

        var library = Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value;
        Assert.Contains(library.Items, item => item.Level == MediaServerItemLevels.Show);
        Assert.Equal(2, library.Items.Count(item => item.Level == MediaServerItemLevels.Episode));
    }

    // ---- watched, playing ----

    [Fact]
    public async Task A_film_marked_watched_by_the_administrator_is_read_as_watched_and_the_mark_is_a_200_from_the_server()
    {
        Assert.Equal(200, R("watched").Exchanges[0].Reply.Status);
        Assert.Equal(1, (int)R("watched").Exchanges[0].Reply.Json()["PlayCount"]!);
        var emby = new Emby();

        var answer = await emby.Client().ListItemsAsync(Server(), "movies", CancellationToken.None);

        var film = Assert.Single(Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value.Items);
        Assert.True(film.Watch.PlayedByAnyone);
        Assert.NotEmpty(film.Watch.PlayedBy);
        Assert.True(emby.Sent("IsPlayed=true"), string.Join(" | ", emby.Calls));
    }

    [Fact]
    public async Task A_film_being_played_is_listed_and_a_stopped_or_idle_server_lists_nobody()
    {
        var emby = new Emby();
        var playing = await emby.Client().ListPlayingAsync(Server(), CancellationToken.None);

        var play = Assert.Single(Assert.IsType<MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported>(playing).Value);
        Assert.True(play.IsMovie);
        foreach (var state in new[] { "sessions-idle", "sessions-stopped" })
        {
            var reply = R(state).Exchanges[^1].Reply;
            var answer = await new Emby((_, _) => reply.ToResponse()).Client().ListPlayingAsync(Server(), CancellationToken.None);
            Assert.Empty(Assert.IsType<MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported>(answer).Value);
        }

        Assert.Equal(204, R("sessions-playing").Exchanges[1].Reply.Status);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_could_not_be_asked_and_is_never_read_as_nobody_playing_or_an_empty_library()
    {
        var client = new EmbyMediaServerClient(new Factory(R("not-answering").NotAnswering()));

        Assert.IsType<MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Unreachable>(await client.ListPlayingAsync(Server(), CancellationToken.None));
        Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Unreachable>(await client.ListItemsAsync(Server(), "movies", CancellationToken.None));
        Assert.False((await client.TestAsync(Server(), CancellationToken.None)).Ok);
    }

    // ---- telling the server about files ----

    [Fact]
    public async Task A_changed_file_is_sent_as_Library_Media_Updated_and_a_204_means_it_was_told()
    {
        var emby = new Emby();

        var answer = await emby.Client().RefreshAsync(Server(), "movies", [new MediaServerPathChange("/media/Movies/Sintel (2010)/Sintel (2010) [WEB 1080p].mkv", MediaServerChangeKind.Created)], CancellationToken.None);

        Assert.False(Assert.IsType<MediaServerAnswer<MediaServerRefresh>.Reported>(answer).Value.UsedFallbackScan);
        var sent = JsonNode.Parse(Assert.Single(emby.Calls)["POST /emby/Library/Media/Updated ".Length..])!["Updates"]!.AsArray();
        Assert.Equal("Created", (string?)Assert.Single(sent)!["UpdateType"]);
        Assert.Equal([204, 204, 204], R("refresh").Exchanges.Select(e => e.Reply.Status));
    }

    [Fact]
    public async Task When_Emby_refuses_the_targeted_call_Deluno_asks_for_a_full_library_scan_instead()
    {
        var refused = R("media-updated-refused").Exchanges[0].Reply;
        Assert.Equal(500, refused.Status);
        var emby = new Emby((request, _) => UnderEmby(request) == "/Library/Media/Updated" ? refused.ToResponse() : UnderEmby(request) == "/Library/Refresh" ? Reply("refresh", 0) : null);

        var answer = await emby.Client().RefreshAsync(Server(), "movies", [new MediaServerPathChange("/media/Movies/X/X.mkv", MediaServerChangeKind.Created)], CancellationToken.None);

        Assert.True(Assert.IsType<MediaServerAnswer<MediaServerRefresh>.Reported>(answer).Value.UsedFallbackScan);
        Assert.True(emby.Sent("POST /emby/Library/Refresh"));
        Assert.Equal(204, R("media-updated-refused").Exchanges[1].Reply.Status);
    }

    [Fact]
    public async Task Removing_an_item_tells_Emby_each_file_is_Deleted_and_never_calls_its_delete()
    {
        var emby = new Emby();
        var item = new MediaServerItem("6", "movies", "Sintel", 2010, "45745", "tt1727587", null, ["/media/Movies/Sintel (2010)/Sintel (2010) [WEB 1080p].mkv"], null, MediaServerWatchState.Unplayed);

        var answer = await emby.Client().RemoveAsync(Server(), item, CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerRemoval>.Reported>(answer);
        Assert.Equal("Deleted", (string?)JsonNode.Parse(emby.Calls.Single()["POST /emby/Library/Media/Updated ".Length..])!["Updates"]![0]!["UpdateType"]);
        Assert.DoesNotContain(emby.Calls, call => call.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_folder_Emby_can_see_is_found_and_the_404_for_one_it_cannot_is_an_unreachable_listing()
    {
        var seen = R("directory-contents").Exchanges[0].Reply;
        var missing = R("directory-contents").Exchanges[1].Reply;
        Assert.Equal(404, missing.Status);
        var emby = new Emby((request, _) => request.RequestUri!.Query.Contains("nowhere", StringComparison.OrdinalIgnoreCase) ? missing.ToResponse() : seen.ToResponse());

        var visible = await emby.Client().TestPathAsync(Server(), "/media/Movies", CancellationToken.None);
        var invisible = await emby.Client().TestPathAsync(Server(), "/nowhere/none", CancellationToken.None);

        Assert.True(Assert.IsType<MediaServerAnswer<MediaServerPathMappingTest>.Reported>(visible).Value.Found);
        Assert.IsType<MediaServerAnswer<MediaServerPathMappingTest>.Unreachable>(invisible);
    }

    // ---- collections ----

    private static MediaServerCollectionRequest Request(string description, bool home, params string[] members)
        => new("movies", "Recorded collection", description, members.Select(id => new MediaServerCollectionMember(id, MediaServerItemLevels.Movie)).ToArray(), home);

    private static HttpResponseMessage NoCollectionYet() => R("library-empty").Exchanges[2].Reply.ToResponse();

    private static HttpResponseMessage? Created(HttpRequestMessage request)
    {
        var recorded = R("collection-sync");
        var path = UnderEmby(request);
        if (path == "/Collections" && request.Method == HttpMethod.Post) return recorded.Exchanges[0].Reply.ToResponse();
        if (path == "/Items" && request.RequestUri!.Query.Contains("Ids=" + CollectionId, StringComparison.Ordinal)) return recorded.Exchanges[1].Reply.ToResponse();
        if (path == $"/Items/{CollectionId}") return recorded.Exchanges[2].Reply.ToResponse();
        if (path.Contains("/FavoriteItems/", StringComparison.Ordinal)) return request.Method == HttpMethod.Post ? recorded.Exchanges[3].Reply.ToResponse() : R("collection-unfavourited").Exchanges[0].Reply.ToResponse();
        return null;
    }

    [Fact]
    public async Task A_new_collection_is_made_with_its_titles_described_and_favourited_onto_the_home_screen_when_asked()
    {
        var emby = new Emby((request, _) => UnderEmby(request) == "/Items" && request.RequestUri!.Query.Contains("IncludeItemTypes=BoxSet", StringComparison.Ordinal) ? NoCollectionYet() : Created(request));

        var answer = await emby.Client().SyncCollectionAsync(Server(), Request("Leaving soon: these go on Friday.", home: true, "6"), CancellationToken.None);

        var sync = Assert.IsType<MediaServerAnswer<MediaServerCollectionSync>.Reported>(answer).Value;
        Assert.Equal(1, sync.Added);
        Assert.True(emby.Sent("POST /emby/Collections?Name=Recorded%20collection&Ids=6"));
        Assert.Equal("Leaving soon: these go on Friday.", (string?)JsonNode.Parse(emby.Calls.Single(call => call.StartsWith($"POST /emby/Items/{CollectionId}", StringComparison.Ordinal))[$"POST /emby/Items/{CollectionId} ".Length..])!["Overview"]);
        Assert.True(emby.Sent($"POST /emby/Users/{UserId}/FavoriteItems/{CollectionId}"), string.Join(" | ", emby.Calls));
    }

    [Fact]
    public async Task A_collection_taken_off_the_home_screen_is_unfavourited_and_Emby_answers_the_state_with_IsFavorite_false()
    {
        Assert.False((bool)R("collection-unfavourited").Single.Reply.Json()["IsFavorite"]!);
        var emby = new Emby((request, _) =>
        {
            var path = UnderEmby(request);
            if (path == "/Items" && request.RequestUri!.Query.Contains("IncludeItemTypes=BoxSet", StringComparison.Ordinal)) return Reply("collection-sync", 6);
            if (path.EndsWith("/Items", StringComparison.Ordinal) && request.RequestUri!.Query.Contains("ParentId", StringComparison.Ordinal)) return Reply("collection-sync", 7);
            return Created(request);
        });

        var answer = await emby.Client().SyncCollectionAsync(Server(), Request("Leaving soon: these go on Friday.", home: false, "6"), CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerCollectionSync>.Reported>(answer);
        Assert.DoesNotContain(emby.Calls, call => call.StartsWith("POST /emby/Users/", StringComparison.Ordinal) && call.Contains("FavoriteItems", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_collection_with_nothing_left_in_it_is_deleted_and_only_the_collection_and_a_second_delete_is_the_servers_204()
    {
        var recorded = R("collection-deleted");
        Assert.Equal(204, recorded.Exchanges[0].Reply.Status);
        Assert.Equal(204, recorded.Exchanges[1].Reply.Status);
        var emby = new Emby((request, _) =>
        {
            if (UnderEmby(request) == "/Items" && request.RequestUri!.Query.Contains("IncludeItemTypes=BoxSet", StringComparison.Ordinal)) return Reply("collection-sync", 6);
            return request.Method == HttpMethod.Delete ? recorded.Exchanges[0].Reply.ToResponse() : null;
        });

        var answer = await emby.Client().SyncCollectionAsync(Server(), Request("Leaving soon", home: false), CancellationToken.None);

        Assert.True(Assert.IsType<MediaServerAnswer<MediaServerCollectionSync>.Reported>(answer).Value.Deleted);
        Assert.Equal([$"DELETE /emby/Items/{CollectionId}"], emby.Calls.Where(call => call.StartsWith("DELETE", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_collection_on_a_server_that_does_not_answer_is_not_updated_and_says_it_could_not_be_reached()
    {
        var down = R("not-answering").NotAnswering();

        var answer = await new EmbyMediaServerClient(new Factory(down)).SyncCollectionAsync(Server(), Request("Leaving soon", home: false, "6"), CancellationToken.None);

        Assert.Contains("could not reach", Assert.IsType<MediaServerAnswer<MediaServerCollectionSync>.Unreachable>(answer).Reason, StringComparison.Ordinal);
    }

    // ---- what the real server did that Deluno does not call ----

    [Fact]
    public void Emby_removes_a_film_by_id_with_204_and_answers_204_again_for_an_id_it_does_not_have_and_makes_and_removes_a_user()
    {
        var deleted = R("item-deleted").Exchanges;
        Assert.Equal([200, 204, 200, 204], deleted.Select(e => e.Reply.Status));
        Assert.DoesNotContain("Recorded Delete", deleted[2].Reply.Body, StringComparison.Ordinal);
        Assert.Contains("Recorded Delete", deleted[0].Reply.Body, StringComparison.Ordinal);
        var user = R("user-created").Exchanges;
        Assert.Equal([200, 204, 200, 204], user.Select(e => e.Reply.Status));
        Assert.Equal(HttpStatusCode.OK, user[0].Reply.StatusCode);
    }

    [Fact]
    public async Task Created_Modified_and_Deleted_files_go_to_the_server_in_one_call_each_with_its_own_kind_in_the_order_given()
    {
        var emby = new Emby();
        MediaServerPathChange[] changes =
        [
            new("/media/Movies/Sintel (2010)/Sintel (2010) [WEB 1080p].mkv", MediaServerChangeKind.Created),
            new("/media/Movies/Sintel (2010)/Sintel (2010) [WEB 720p].mkv", MediaServerChangeKind.Deleted),
            new("/media/Movies/Sintel (2010)/Sintel (2010).nfo", MediaServerChangeKind.Modified)
        ];

        var answer = await emby.Client().RefreshAsync(Server(), "movies", changes, CancellationToken.None);

        Assert.False(Assert.IsType<MediaServerAnswer<MediaServerRefresh>.Reported>(answer).Value.UsedFallbackScan);
        var sent = JsonNode.Parse(Assert.Single(emby.Calls)["POST /emby/Library/Media/Updated ".Length..])!["Updates"]!.AsArray();
        Assert.Equal(changes.Select(change => change.Path), sent.Select(update => (string?)update!["Path"]));
        Assert.Equal(["Created", "Deleted", "Modified"], sent.Select(update => (string?)update!["UpdateType"]));
    }
}
