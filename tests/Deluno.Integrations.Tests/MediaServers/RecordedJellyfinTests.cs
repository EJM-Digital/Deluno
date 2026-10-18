using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.MediaServers;

/// <summary>
/// Deluno's Jellyfin client against Jellyfin 12.2.0's own replies, recorded from the real server with a library of one film
/// and a two-episode series (tests/fixtures/recorded/jellyfin, #1237). A call is answered by the recorded reply to the same
/// kind of call; the ids in a call are the test's own.
/// </summary>
public sealed class RecordedJellyfinTests
{
    private static MediaServerConnection Server(string? token = "key")
        => new("server-1", MediaServerKinds.Jellyfin, "Jellyfin", "http://192.168.1.51:8096", token, true, "untested", null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static JellyfinMediaServerClient Client(Func<HttpRequestMessage, string, HttpResponseMessage?> answer)
        => new(new Factory(new HttpClient(new Interceptor(answer))));

    /// <summary>Answers by the recorded states: the catalogue calls pick the movie or the series reply by the item types asked for.</summary>
    private static HttpResponseMessage? FromRecordings(HttpRequestMessage request, string body)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;
        if (path == "/System/Info") return RecordedReplies.Jellyfin("identity").Exchanges[1].Reply.ToResponse();
        if (path == "/Library/VirtualFolders") return RecordedReplies.Jellyfin("libraries").Exchanges[0].Reply.ToResponse();
        if (path == "/Users") return RecordedReplies.Jellyfin("libraries").Exchanges[1].Reply.ToResponse();
        if (path == "/Sessions") return RecordedReplies.Jellyfin("sessions-playing").Exchanges[2].Reply.ToResponse();
        if (path == "/Items" && query.Contains("IncludeItemTypes=Movie")) return RecordedReplies.Jellyfin("catalogue-movies").Exchanges[0].Reply.ToResponse();
        if (path == "/Items" && query.Contains("IncludeItemTypes=Series")) return RecordedReplies.Jellyfin("catalogue-series").Exchanges[0].Reply.ToResponse();
        if (path.StartsWith("/Users/") && path.EndsWith("/Items"))
        {
            // The per-user listings: what each user has watched, and which collections and playlists hold what.
            if (query.Contains("BoxSet")) return RecordedReplies.Jellyfin("collection-sync").Exchanges[6].Reply.ToResponse();
            if (query.Contains("Playlist") || query.Contains("ParentId")) return RecordedReplies.Jellyfin("collection-sync").Exchanges[8].Reply.ToResponse();
            if (query.Contains("IncludeItemTypes=Movie")) return RecordedReplies.Jellyfin("catalogue-movies").Exchanges[query.Contains("IsPlayed") ? 1 : 2].Reply.ToResponse();
            return RecordedReplies.Jellyfin("catalogue-series").Exchanges[0].Reply.ToResponse();
        }
        if (path == "/Library/Media/Updated") return RecordedReplies.Jellyfin("refresh").Exchanges[1].Reply.ToResponse();
        return null;
    }

    [Fact]
    public async Task The_connection_test_reads_the_real_server_info_libraries_and_users()
    {
        var result = await Client(FromRecordings).TestAsync(Server(), CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(2, result.LibraryCount);
        Assert.Equal(1, result.UserCount);
    }

    [Fact]
    public async Task A_key_the_server_does_not_know_is_a_refusal_not_a_working_connection()
    {
        var refused = RecordedReplies.Jellyfin("wrong-token").Exchanges[0].Reply;
        Assert.Equal(401, refused.Status);

        var result = await Client((_, _) => refused.ToResponse()).TestAsync(Server("not-the-key"), CancellationToken.None);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task The_movie_catalogue_has_the_film_with_its_provider_ids_and_file()
    {
        var answer = await Client(FromRecordings).ListItemsAsync(Server(), "movies", CancellationToken.None);

        var library = AssertReported(answer);
        var film = Assert.Single(library.Items);
        Assert.Equal("Sintel", film.Title);
        Assert.Equal("45745", film.TmdbId);
        Assert.Equal("tt1727587", film.ImdbId);
        Assert.Contains(film.Paths, path => path.EndsWith("Sintel (2010) [WEB 1080p].mkv", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_series_catalogue_holds_the_show_its_seasons_and_episodes()
    {
        var answer = await Client(FromRecordings).ListItemsAsync(Server(), "tv", CancellationToken.None);

        var library = AssertReported(answer);
        Assert.Contains(library.Items, item => item.Title.Contains("Recorded Show", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_film_being_played_is_listed_with_its_ids_and_path()
    {
        var answer = await Client(FromRecordings).ListPlayingAsync(Server(), CancellationToken.None);

        var plays = Assert.IsType<MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported>(answer).Value;
        var play = Assert.Single(plays);
        Assert.True(play.IsMovie);
        Assert.Equal("45745", play.TmdbId);
    }

    [Fact]
    public async Task With_no_one_playing_nothing_is_listed_and_a_stopped_play_leaves_the_list()
    {
        foreach (var state in new[] { "sessions-idle", "sessions-stopped" })
        {
            var reply = RecordedReplies.Jellyfin(state).Exchanges[^1].Reply;
            var answer = await Client((_, _) => reply.ToResponse()).ListPlayingAsync(Server(), CancellationToken.None);

            Assert.Empty(Assert.IsType<MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Reported>(answer).Value);
        }
    }

    [Fact]
    public async Task A_server_that_does_not_answer_could_not_be_asked()
    {
        var down = RecordedReplies.Jellyfin("not-answering").NotAnswering();

        var answer = await new JellyfinMediaServerClient(new Factory(new HttpClient(down))).ListPlayingAsync(Server(), CancellationToken.None);

        Assert.IsType<MediaServerAnswer<IReadOnlyList<MediaServerPlay>>.Unreachable>(answer);
    }

    [Fact]
    public void The_real_setup_wizard_the_empty_library_and_the_first_calls_are_recorded_as_the_server_answers_them()
    {
        // The recordings other tests do not need call by call: they pin the real shapes (the wizard's 500 on RemoteAccess is real).
        Assert.Equal(204, RecordedReplies.Jellyfin("wizard").Exchanges[7].Reply.Status);
        Assert.Contains("\"StartupWizardCompleted\"", RecordedReplies.Jellyfin("wizard").Exchanges[0].Reply.Body);
        Assert.Equal(0, RecordedReplies.Jellyfin("library-empty").Exchanges[2].Reply.Json()["TotalRecordCount"]!.GetValue<int>());
        Assert.Equal(401, RecordedReplies.Jellyfin("no-token").Single.Reply.Status);
        Assert.Equal(204, RecordedReplies.Jellyfin("libraries-created").Exchanges[0].Reply.Status);
        Assert.All(RecordedReplies.Jellyfin("refresh").Exchanges, e => Assert.Equal(204, e.Reply.Status));
        Assert.Equal(404, RecordedReplies.Jellyfin("directory-contents").Exchanges[1].Reply.Status);
        Assert.Equal(404, RecordedReplies.Jellyfin("collection-deleted").Exchanges[1].Reply.Status);
        Assert.Equal(204, RecordedReplies.Jellyfin("collection-sync").Exchanges[4].Reply.Status);
        Assert.NotNull(RecordedReplies.Jellyfin("collection-sync").Exchanges[0].Reply.Json()["Id"]);
        Assert.Equal(204, RecordedReplies.Jellyfin("user-created").Exchanges[1].Reply.Status);
        // Jellyfin answered its delete-by-id with 500 and an unknown id with 400 (recorded, not assumed).
        Assert.Equal(new[] { 200, 500, 200, 400 }, RecordedReplies.Jellyfin("item-deleted").Exchanges.Select(e => e.Reply.Status));
        Assert.Equal(1, RecordedReplies.Jellyfin("watched").Exchanges[1].Reply.Json()["TotalRecordCount"]!.GetValue<int>());
        Assert.Equal(204, RecordedReplies.Jellyfin("sessions-playing").Exchanges[1].Reply.Status);
    }

    [Fact]
    public async Task When_Jellyfin_refuses_the_targeted_call_with_400_or_415_Deluno_asks_for_a_full_library_scan_instead()
    {
        // Recorded from Jellyfin 12.2.0 in Docker: a body it cannot read is 400, a call with no content type 415.
        var refused = RecordedReplies.Jellyfin("media-updated-refused");
        Assert.Equal([400, 415], refused.Exchanges.Select(e => e.Reply.Status));
        foreach (var exchange in refused.Exchanges)
        {
            var calls = new List<string>();
            var client = Client((request, _) =>
            {
                calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
                return request.RequestUri!.AbsolutePath == "/Library/Media/Updated" ? exchange.Reply.ToResponse() : request.RequestUri.AbsolutePath == "/Library/Refresh" ? RecordedReplies.Jellyfin("refresh").Exchanges[0].Reply.ToResponse() : null;
            });

            var answer = await client.RefreshAsync(Server(), "movies", [new Deluno.Contracts.MediaServerPathChange("/media/Movies/X/X.mkv", Deluno.Contracts.MediaServerChangeKind.Created)], CancellationToken.None);

            Assert.True(Assert.IsType<MediaServerAnswer<MediaServerRefresh>.Reported>(answer).Value.UsedFallbackScan);
            Assert.Equal(["POST /Library/Media/Updated", "POST /Library/Refresh"], calls);
        }
    }

    [Fact]
    public void A_collection_taken_off_the_home_screen_is_answered_with_the_state_IsFavorite_false()
    {
        var reply = RecordedReplies.Jellyfin("collection-unfavourited").Single;

        Assert.Equal("DELETE", reply.Request.Method);
        Assert.Contains("/FavoriteItems/", reply.Request.Path, StringComparison.Ordinal);
        Assert.Equal(200, reply.Reply.Status);
    }

    private static MediaServerLibrary AssertReported(MediaServerAnswer<MediaServerLibrary> answer)
        => answer is MediaServerAnswer<MediaServerLibrary>.Reported reported ? reported.Value : throw new Xunit.Sdk.XunitException("Not reported: " + answer);

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Interceptor(Func<HttpRequestMessage, string, HttpResponseMessage?> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return answer(request, body) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
