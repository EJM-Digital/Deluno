using System.Net;
using System.Text.Json.Nodes;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.MediaServers;

/// <summary>
/// #1301: each Jellyfin operation of the shared Jellyfin/Emby client replayed from Jellyfin 12.2.0's own recorded replies, with the
/// calls Deluno sent asserted as well as what it made of the answers. A reply that is the answer to a different address than the
/// one Deluno asked (a collection listing recorded for the user's view, answering the search view) is said so where it is used.
/// </summary>
public sealed class JellyfinOperationsOnRecordingsTests
{
    private const string UserId = "03d927573b5e4cd9942b61f120b5f9a4";
    private const string CollectionId = "18da5b93435bf32b1375cc6efe0ca347";
    private const string Sintel = "3df4dba3fab3ef172511446b46f3e8a0";

    private static MediaServerConnection Server(string? token = "key")
        => new("server-1", MediaServerKinds.Jellyfin, "Jellyfin", "http://192.168.1.51:8096", token, true, "untested", null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>A Jellyfin answering from its recordings, noting every call made to it (method, address, body).</summary>
    private sealed class Jellyfin
    {
        public List<string> Calls { get; } = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        public JellyfinMediaServerClient Client(Func<HttpRequestMessage, string, HttpResponseMessage?>? answer = null)
            => new(new Factory(RecordedReplies.Handler("jellyfin", [], (request, body) =>
            {
                Calls.Add($"{request.Method} {request.RequestUri!.PathAndQuery} {body}".TrimEnd());
                Requests.Add(request);
                return answer?.Invoke(request, body) ?? RecordedReply(request);
            })));

        public bool Sent(string fragment) => Calls.Any(call => call.Contains(fragment, StringComparison.Ordinal));

        public int Count(string fragment) => Calls.Count(call => call.Contains(fragment, StringComparison.Ordinal));
    }

    /// <summary>The recorded reply to the kind of call: the catalogue calls pick the movie or the series reply by the item types asked for.</summary>
    private static HttpResponseMessage RecordedReply(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;
        HttpResponseMessage? reply = null;
        if (path == "/System/Info") reply = RecordedReplies.Jellyfin("identity").Exchanges[1].Reply.ToResponse();
        else if (path == "/Library/VirtualFolders") reply = RecordedReplies.Jellyfin("libraries").Exchanges[0].Reply.ToResponse();
        else if (path == "/Users") reply = RecordedReplies.Jellyfin("libraries").Exchanges[1].Reply.ToResponse();
        else if (path == "/Items" && query.Contains("IncludeItemTypes=Movie")) reply = RecordedReplies.Jellyfin("catalogue-movies").Exchanges[0].Reply.ToResponse();
        else if (path == "/Items" && query.Contains("IncludeItemTypes=Series")) reply = RecordedReplies.Jellyfin("catalogue-series").Exchanges[0].Reply.ToResponse();
        else if (path.StartsWith("/Users/", StringComparison.Ordinal) && path.EndsWith("/Items", StringComparison.Ordinal))
        {
            if (query.Contains("IncludeItemTypes=BoxSet")) reply = RecordedReplies.Jellyfin("collection-sync").Exchanges[6].Reply.ToResponse();
            else if (query.Contains("IncludeItemTypes=Playlist")) reply = RecordedReplies.Jellyfin("collection-sync").Exchanges[8].Reply.ToResponse();
            else if (query.Contains("ParentId")) reply = RecordedReplies.Jellyfin("collection-sync").Exchanges[7].Reply.ToResponse();
            else if (query.Contains("IsPlayed")) reply = RecordedReplies.Jellyfin("watched").Exchanges[1].Reply.ToResponse();
            else reply = RecordedReplies.Jellyfin("watched").Exchanges[2].Reply.ToResponse();
        }
        else if (path == "/Library/Media/Updated") reply = RecordedReplies.Jellyfin("refresh").Exchanges[1].Reply.ToResponse();
        return reply ?? throw new InvalidOperationException($"No Jellyfin recording answers {request.Method} {path}{query}.");
    }

    // ---- the token header (Jellyfin 10.11 / 12 refuse the legacy one) ----

    [Fact]
    public async Task Jellyfin_is_sent_the_key_as_a_MediaBrowser_Token_authorization_and_never_the_legacy_X_Emby_Token()
    {
        var jellyfin = new Jellyfin();

        var result = await jellyfin.Client().TestAsync(Server("the-key"), CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.NotEmpty(jellyfin.Requests);
        Assert.All(jellyfin.Requests, request =>
        {
            Assert.Equal("MediaBrowser Token=\"the-key\"", Assert.Single(request.Headers.GetValues("Authorization")));
            Assert.False(request.Headers.Contains("X-Emby-Token"));
            Assert.False(request.RequestUri!.AbsolutePath.StartsWith("/emby", StringComparison.OrdinalIgnoreCase));
        });
        Assert.Equal("12.2.0", result.Version);
    }

    // ---- wrong credentials (401): refused the token, never an empty list ----

    [Fact]
    public async Task A_wrong_key_is_a_refused_token_on_the_test_and_a_listing_answers_unreachable_never_an_empty_library()
    {
        var refused = RecordedReplies.Jellyfin("wrong-token").Exchanges[0].Reply;
        var jellyfin = new Jellyfin();
        var client = jellyfin.Client((_, _) => refused.ToResponse());

        var test = await client.TestAsync(Server("wrong"), CancellationToken.None);
        var listing = await client.ListItemsAsync(Server("wrong"), "movies", CancellationToken.None);

        Assert.False(test.Ok);
        Assert.Contains("refused the token (HTTP 401)", test.Message);
        var unreachable = Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Unreachable>(listing);
        Assert.Contains("refused the token", unreachable.Reason);
    }

    [Fact]
    public async Task A_key_that_can_read_the_server_but_not_list_its_users_is_degraded_with_advice_to_use_an_administrators_key_and_the_version_is_still_read()
    {
        // The recorded 401 Jellyfin gave for /Users with a key it does not accept, with the server info and libraries it did give.
        var noUsers = RecordedReplies.Jellyfin("wrong-token").Exchanges[1].Reply;
        var jellyfin = new Jellyfin();
        var client = jellyfin.Client((request, _) => request.RequestUri!.AbsolutePath == "/Users" ? noUsers.ToResponse() : null);

        var result = await client.TestAsync(Server(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("degraded", result.Status);
        Assert.Contains("Use an administrator's API key", result.Message);
        Assert.Equal("12.2.0", result.Version);
    }

    // ---- a saved token that cannot be read here ----

    [Fact]
    public async Task A_saved_token_that_cannot_be_decrypted_makes_the_test_say_to_enter_it_again_without_calling_the_server()
    {
        var jellyfin = new Jellyfin();
        var server = Server() with { Token = null, SecretUnreadable = true, SecretProblem = "The saved API key was stored on another computer and can't be read here. Enter it again." };

        var result = await jellyfin.Client().TestAsync(server, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("degraded", result.Status);
        Assert.Contains("Enter it again", result.Message);
        Assert.Empty(jellyfin.Calls);
    }

    // ---- a listing that breaks on page 2 ----

    [Fact]
    public async Task A_listing_that_breaks_on_page_two_is_unreachable_not_half_a_library()
    {
        // A first page of 500 films that says there are 600: Deluno must ask for the rest, and Jellyfin's recorded 500 on that call
        // (its answer to a call it could not serve) leaves the library unread.
        var full = RecordedReplies.Jellyfin("catalogue-movies").Body("Items", ("+Items", 500), ("TotalRecordCount", 600));
        var failure = RecordedReplies.Jellyfin("item-deleted").Exchanges[1].Reply;
        var jellyfin = new Jellyfin();
        var client = jellyfin.Client((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath != "/Items") return null;
            return request.RequestUri.Query.Contains("StartIndex=0", StringComparison.Ordinal) ? RecordedReplies.Json(full) : failure.ToResponse();
        });

        var answer = await client.ListItemsAsync(Server(), "movies", CancellationToken.None);

        var unreachable = Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Unreachable>(answer);
        Assert.Contains("answered HTTP 500", unreachable.Reason);
        Assert.True(jellyfin.Sent("StartIndex=500"), "page two was asked for");
    }

    // ---- what each person has watched; an unreachable server is unknown, not nobody ----

    [Fact]
    public async Task What_a_user_has_watched_is_read_from_the_server_and_a_server_that_does_not_answer_is_unknown_not_nobody_watched()
    {
        var jellyfin = new Jellyfin();
        var listed = await jellyfin.Client().ListItemsAsync(Server(), "movies", CancellationToken.None);
        var down = RecordedReplies.Jellyfin("not-answering").NotAnswering();
        var unknown = await new JellyfinMediaServerClient(new Factory(down)).ListItemsAsync(Server(), "movies", CancellationToken.None);

        var film = Assert.Single(Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(listed).Value.Items);
        Assert.Equal("Sintel", film.Title);
        Assert.True(film.Watch.PlayedByAnyone, "the recorded user finished Sintel");
        Assert.NotEmpty(film.Watch.PlayedBy);
        Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Unreachable>(unknown);
    }

    // ---- removing an item tells the server the files are gone, and never calls its delete ----

    [Fact]
    public async Task Removing_an_item_posts_Library_Media_Updated_with_each_file_Deleted_and_never_calls_the_servers_delete()
    {
        var jellyfin = new Jellyfin();
        var item = new MediaServerItem(Sintel, "movies", "Sintel", 2010, "45745", "tt1727587", null, ["C:\\Movies\\Sintel (2010)\\Sintel (2010).mkv"], null, MediaServerWatchState.Unplayed);

        var answer = await jellyfin.Client().RemoveAsync(Server(), item, CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerRemoval>.Reported>(answer);
        var sent = JsonNode.Parse(jellyfin.Calls.Single(call => call.StartsWith("POST /Library/Media/Updated", StringComparison.Ordinal))["POST /Library/Media/Updated ".Length..])!;
        var update = Assert.Single(sent["Updates"]!.AsArray())!;
        Assert.Equal("Deleted", (string?)update["UpdateType"]);
        Assert.Equal("C:\\Movies\\Sintel (2010)\\Sintel (2010).mkv", (string?)update["Path"]);
        Assert.DoesNotContain(jellyfin.Calls, call => call.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_item_with_no_known_path_cannot_be_reported_removed_and_nothing_is_sent()
    {
        var jellyfin = new Jellyfin();
        var item = new MediaServerItem(Sintel, "movies", "Sintel", 2010, null, null, null, [], null, MediaServerWatchState.Unplayed);

        var answer = await jellyfin.Client().RemoveAsync(Server(), item, CancellationToken.None);

        var notSupported = Assert.IsType<MediaServerAnswer<MediaServerRemoval>.NotSupported>(answer);
        Assert.Contains("did not say where Sintel is stored", notSupported.Reason);
        Assert.Empty(jellyfin.Calls);
    }

    // ---- the leaving-soon collection ----

    private static MediaServerCollectionRequest Request(string description, bool home, params string[] members)
        => new("movies", "Recorded collection", description, members.Select(id => new MediaServerCollectionMember(id, MediaServerItemLevels.Movie)).ToArray(), home);

    /// <summary>The recorded empty listing a search for a collection by name gets when there is none.</summary>
    private static HttpResponseMessage NoCollectionYet() => RecordedReplies.Jellyfin("library-empty").Exchanges[2].Reply.ToResponse();

    [Fact]
    public async Task A_new_collection_is_created_with_its_titles_given_its_description_and_favourited_onto_the_home_screen_when_asked()
    {
        var jellyfin = new Jellyfin();
        var client = jellyfin.Client((request, _) => request.RequestUri!.AbsolutePath == "/Items" && request.RequestUri.Query.Contains("IncludeItemTypes=BoxSet") ? NoCollectionYet() : Created(request));

        var answer = await client.SyncCollectionAsync(Server(), Request("Leaving soon: these go on Friday.", home: true, Sintel), CancellationToken.None);

        var sync = Assert.IsType<MediaServerAnswer<MediaServerCollectionSync>.Reported>(answer).Value;
        Assert.Equal(1, sync.Added);
        Assert.False(sync.Deleted);
        Assert.True(jellyfin.Sent($"POST /Collections?Name=Recorded%20collection&Ids={Sintel}"));
        var described = jellyfin.Calls.Single(call => call.StartsWith($"POST /Items/{CollectionId}", StringComparison.Ordinal));
        Assert.Equal("Leaving soon: these go on Friday.", (string?)JsonNode.Parse(described[$"POST /Items/{CollectionId} ".Length..])!["Overview"]);
        Assert.True(jellyfin.Sent($"POST /Users/{UserId}/FavoriteItems/{CollectionId}"), "favourited onto the home screen");
    }

    /// <summary>The recorded replies of creating a collection: the create, the read-back of it, the save of it and the favourite.</summary>
    private static HttpResponseMessage? Created(HttpRequestMessage request)
    {
        var recorded = RecordedReplies.Jellyfin("collection-sync");
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/Collections" && request.Method == HttpMethod.Post) return recorded.Exchanges[0].Reply.ToResponse();
        if (path == "/Items" && request.RequestUri.Query.Contains("Ids=" + CollectionId)) return recorded.Exchanges[1].Reply.ToResponse();
        if (path == $"/Items/{CollectionId}") return recorded.Exchanges[2].Reply.ToResponse();
        if (path.Contains("/FavoriteItems/", StringComparison.Ordinal)) return recorded.Exchanges[3].Reply.ToResponse();
        return null;
    }

    [Fact]
    public async Task A_collection_a_person_edited_by_hand_is_put_back_its_description_is_set_again_and_a_member_added_by_hand_is_taken_out()
    {
        var jellyfin = new Jellyfin();
        const string added = "9b53834449c3ce873cb7bb395a04d3ed";
        var client = jellyfin.Client((request, _) =>
        {
            var recorded = RecordedReplies.Jellyfin("collection-sync");
            var path = request.RequestUri!.AbsolutePath;
            // The collection exists, with a description of someone's own, and holds Sintel plus a title added by hand.
            if (path == "/Items" && request.RequestUri.Query.Contains("IncludeItemTypes=BoxSet")) return recorded.Exchanges[6].Reply.ToResponse();
            if (path.EndsWith("/Items", StringComparison.Ordinal) && request.RequestUri.Query.Contains("ParentId"))
                return RecordedReplies.Json(recorded.Body("ParentId", ("+Items", 2), ("Items.1.Id", added)));
            if (path == $"/Collections/{CollectionId}/Items") return recorded.Exchanges[request.Method == HttpMethod.Delete ? 5 : 4].Reply.ToResponse();
            return Created(request);
        });

        var answer = await client.SyncCollectionAsync(Server(), Request("Leaving soon: these go on Friday.", home: false, Sintel), CancellationToken.None);

        var sync = Assert.IsType<MediaServerAnswer<MediaServerCollectionSync>.Reported>(answer).Value;
        Assert.Equal(0, sync.Added);
        Assert.Equal(1, sync.Removed);
        Assert.True(jellyfin.Sent($"DELETE /Collections/{CollectionId}/Items?Ids={added}"), "the hand-added title is taken out");
        Assert.False(jellyfin.Sent($"DELETE /Items/{CollectionId}"), "the collection itself is not deleted");
        Assert.True(jellyfin.Sent($"POST /Items/{CollectionId}"), "the description is put back");
    }

    [Fact]
    public async Task A_collection_with_nothing_left_in_it_is_deleted_and_only_the_collection_never_a_title_in_it()
    {
        var recorded = RecordedReplies.Jellyfin("collection-deleted");
        var jellyfin = new Jellyfin();
        var client = jellyfin.Client((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/Items" && request.RequestUri.Query.Contains("IncludeItemTypes=BoxSet"))
                return RecordedReplies.Jellyfin("collection-sync").Exchanges[6].Reply.ToResponse();
            return request.Method == HttpMethod.Delete ? recorded.Exchanges[0].Reply.ToResponse() : null;
        });

        var answer = await client.SyncCollectionAsync(Server(), Request("Leaving soon", home: false), CancellationToken.None);

        var sync = Assert.IsType<MediaServerAnswer<MediaServerCollectionSync>.Reported>(answer).Value;
        Assert.True(sync.Deleted);
        var deletes = jellyfin.Calls.Where(call => call.StartsWith("DELETE", StringComparison.Ordinal)).ToArray();
        Assert.Equal([$"DELETE /Items/{CollectionId}"], deletes);
    }

    [Fact]
    public async Task A_collection_on_a_server_that_does_not_answer_is_not_updated_and_says_it_could_not_be_reached()
    {
        var down = RecordedReplies.Jellyfin("not-answering").NotAnswering();

        var answer = await new JellyfinMediaServerClient(new Factory(down)).SyncCollectionAsync(Server(), Request("Leaving soon", home: false, Sintel), CancellationToken.None);

        var unreachable = Assert.IsType<MediaServerAnswer<MediaServerCollectionSync>.Unreachable>(answer);
        Assert.Contains("could not reach", unreachable.Reason);
    }

    // ---- telling the server about files ----

    [Fact]
    public async Task A_path_the_server_cannot_see_is_reported_as_not_a_folder_it_has_and_the_recorded_404_is_an_unreachable_listing()
    {
        var jellyfin = new Jellyfin();
        var seen = RecordedReplies.Jellyfin("directory-contents").Exchanges[0].Reply;
        var missing = RecordedReplies.Jellyfin("directory-contents").Exchanges[1].Reply;
        var client = jellyfin.Client((request, _) => request.RequestUri!.Query.Contains("nowhere", StringComparison.OrdinalIgnoreCase) ? missing.ToResponse() : seen.ToResponse());

        var visible = await client.TestPathAsync(Server(), "C:\\golden\\recorded-apps\\media\\Movies", CancellationToken.None);
        var invisible = await client.TestPathAsync(Server(), "C:\\nowhere\\none", CancellationToken.None);

        Assert.True(Assert.IsType<MediaServerAnswer<MediaServerPathMappingTest>.Reported>(visible).Value.Found);
        Assert.IsType<MediaServerAnswer<MediaServerPathMappingTest>.Unreachable>(invisible);
    }

    // ---- a title removed with its files: the server is told each file is Deleted ----

    [Fact]
    public async Task A_removal_with_the_files_deleted_tells_the_server_each_file_is_Deleted_in_one_call_and_nothing_else()
    {
        var jellyfin = new Jellyfin();
        MediaServerPathChange[] changes =
        [
            new("C:\\Movies\\Sintel (2010)\\Sintel (2010).mkv", MediaServerChangeKind.Deleted),
            new("C:\\Movies\\Sintel (2010)\\Sintel (2010).nfo", MediaServerChangeKind.Deleted)
        ];

        var answer = await jellyfin.Client().RefreshAsync(Server(), "movies", changes, CancellationToken.None);

        var refreshed = Assert.IsType<MediaServerAnswer<MediaServerRefresh>.Reported>(answer).Value;
        Assert.False(refreshed.UsedFallbackScan);
        var sent = JsonNode.Parse(Assert.Single(jellyfin.Calls)["POST /Library/Media/Updated ".Length..])!["Updates"]!.AsArray();
        Assert.Equal(2, sent.Count);
        Assert.All(sent, update => Assert.Equal("Deleted", (string?)update!["UpdateType"]));
        Assert.Equal(changes.Select(change => change.Path), sent.Select(update => (string?)update!["Path"]));
    }

    [Fact]
    public async Task A_removal_that_deleted_no_files_sends_the_server_nothing()
    {
        var jellyfin = new Jellyfin();

        var answer = await jellyfin.Client().RefreshAsync(Server(), "movies", [], CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerRefresh>.Reported>(answer);
        Assert.Empty(jellyfin.Calls);
    }
}
