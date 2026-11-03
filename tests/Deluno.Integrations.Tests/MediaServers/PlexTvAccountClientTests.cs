using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.MediaServers;

namespace Deluno.Integrations.Tests.MediaServers;

/// <summary>
/// #493, #1343: what Plex's client makes of plex.tv's account answers (the account, its friends, shared servers and home users, an owner
/// token, and the ratings, favourites and watchlist read with it). These cannot be recorded: plex.tv answers them only to a claimed server
/// and a token that names its owner, and the owner decided on 1 November 2026 not to create outside accounts
/// (docs/testing/account-requests.md, "Waived until an account exists"). plex.tv's own refusal of a request with no token is recorded
/// (tests/fixtures/recorded/plex, plex-tv-no-token) and replayed in PlexOnRecordingsTests. Every other answer here is written by the test.
/// </summary>
public sealed class PlexTvAccountClientTests
{
    private static MediaServerConnection Server(string kind, string baseUrl = "http://192.168.1.51:8096")
        => new("server-1", kind, "Rig " + kind, baseUrl, "token-123", true, "untested", null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private const string SoloAccounts = """{"MediaContainer":{"Account":[{"id":0,"name":""},{"id":1,"name":"jampat"}]}}""";
    private const string OwnerMe = """{"id":987654,"username":"jampat"}""";
    private const string OwnedServer = """[{"name":"Rig","clientIdentifier":"machine-1","owned":true}]""";
    private const string EmptyList = """<MediaContainer size="0"></MediaContainer>""";
    private const string OwnerOnlyHome = """<MediaContainer size="1"><User id="987654" title="jampat" admin="1"/></MediaContainer>""";

    /// <summary>A Plex whose owner token can be asked; each part can be swapped for what a household really looks like. Null = that call fails.</summary>
    private static async Task<bool> PlexAccountIsEveryoneAsync(
        string? accounts = SoloAccounts, string? shared = EmptyList, string? friends = EmptyList, string? home = OwnerOnlyHome, string? me = OwnerMe, string? resources = OwnedServer)
    {
        HttpResponseMessage Answer(string? body) => body is null ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Json(body);
        var client = new PlexMediaServerClient(Factory(request => request.RequestUri!.Host switch
        {
            "discover.provider.plex.tv" => Json("""{"MediaContainer":{"totalSize":0}}"""),
            "plex.tv" => request.RequestUri.AbsolutePath switch
            {
                "/api/v2/user" => Answer(me),
                "/api/v2/resources" => Answer(resources),
                "/api/servers/machine-1/shared_servers" => Answer(shared),
                "/api/users" => Answer(friends),
                "/api/home/users" => Answer(home),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            },
            _ => request.RequestUri.AbsolutePath switch
            {
                "/" => Json("""{"MediaContainer":{"machineIdentifier":"machine-1","friendlyName":"Rig"}}"""),
                "/library/metadata/101" => Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"101"}]}}"""),
                "/playlists" => Json("""{"MediaContainer":{"totalSize":0}}"""),
                "/library/sections" => Json("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie"}]}}"""),
                "/myplex/account" => Json("""{"MyPlex":{"username":"jampat"}}"""),
                "/accounts" => Answer(accounts),
                "/library/sections/1/all" => Json("""{"MediaContainer":{"totalSize":1,"Metadata":[{"ratingKey":"101","type":"movie","title":"Sintel","year":2010,"viewCount":1}]}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            }
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), "movies", CancellationToken.None);

        return Assert.Single(Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value.Items).Watch.PerUserKnown;
    }

    [Fact]
    public async Task Plex_says_the_account_is_everyone_only_when_plex_tv_and_the_server_both_show_nobody_else()
        => Assert.True(await PlexAccountIsEveryoneAsync());

    [Fact]
    public async Task A_friend_the_server_has_not_listed_yet_but_plex_tv_says_has_the_server_is_not_ignored()
        => Assert.False(await PlexAccountIsEveryoneAsync(shared: """<MediaContainer size="1"><SharedServer id="9" userID="22" username="ben"/></MediaContainer>"""));

    [Fact]
    public async Task A_friend_on_the_friends_list_means_not_everyone()
        => Assert.False(await PlexAccountIsEveryoneAsync(friends: """<MediaContainer size="1"><User id="22" title="ben"/></MediaContainer>"""));

    [Fact]
    public async Task A_home_user_means_not_everyone()
        => Assert.False(await PlexAccountIsEveryoneAsync(home: """<MediaContainer size="2"><User id="987654" admin="1"/><User id="33" title="Kids" restricted="1"/></MediaContainer>"""));

    [Fact]
    public async Task A_second_account_in_the_servers_own_table_means_not_everyone()
        => Assert.False(await PlexAccountIsEveryoneAsync(accounts: """{"MediaContainer":{"Account":[{"id":0,"name":""},{"id":1,"name":"jampat"},{"id":2,"name":"ben"}]}}"""));

    [Fact]
    public async Task A_single_account_that_is_not_the_connected_one_means_not_everyone()
        => Assert.False(await PlexAccountIsEveryoneAsync(accounts: """{"MediaContainer":{"Account":[{"id":0,"name":""},{"id":7,"name":"someone"}]}}"""));

    [Fact]
    public async Task The_servers_owner_is_local_id_one_whatever_plex_tv_calls_them_when_the_name_is_theirs()
        => Assert.True(await PlexAccountIsEveryoneAsync(accounts: """{"MediaContainer":{"Account":[{"id":0,"name":""},{"id":1,"name":"JamPat"}]}}"""));

    [Fact]
    public async Task Local_id_one_with_someone_elses_name_means_not_everyone()
        => Assert.False(await PlexAccountIsEveryoneAsync(accounts: """{"MediaContainer":{"Account":[{"id":0,"name":""},{"id":1,"name":"ben"}]}}"""));

    [Fact]
    public async Task A_token_whose_account_does_not_own_the_server_means_not_everyone()
    {
        Assert.False(await PlexAccountIsEveryoneAsync(resources: """[{"clientIdentifier":"machine-1","owned":false}]"""));
        Assert.False(await PlexAccountIsEveryoneAsync(resources: """[{"clientIdentifier":"other","owned":true}]"""));
    }

    [Theory]
    [InlineData("accounts")]
    [InlineData("shared")]
    [InlineData("friends")]
    [InlineData("home")]
    [InlineData("me")]
    [InlineData("resources")]
    public async Task Any_call_that_fails_means_not_everyone(string failing)
        => Assert.False(await PlexAccountIsEveryoneAsync(
            accounts: failing == "accounts" ? null : SoloAccounts,
            shared: failing == "shared" ? null : EmptyList,
            friends: failing == "friends" ? null : EmptyList,
            home: failing == "home" ? null : OwnerOnlyHome,
            me: failing == "me" ? null : OwnerMe,
            resources: failing == "resources" ? null : OwnedServer));

    [Fact]
    public async Task Unreadable_answers_mean_not_everyone()
    {
        Assert.False(await PlexAccountIsEveryoneAsync(shared: "not xml"));
        Assert.False(await PlexAccountIsEveryoneAsync(accounts: "not json"));
        Assert.False(await PlexAccountIsEveryoneAsync(accounts: """{"MediaContainer":{}}"""));
    }

    [Fact]
    public async Task Plex_reads_ids_files_scores_and_one_account_watch_state()
    {
        var client = new PlexMediaServerClient(Factory(request =>
        {
            Assert.Equal("token-123", Assert.Single(request.Headers.GetValues("X-Plex-Token")));
            if (request.RequestUri!.Host == "discover.provider.plex.tv")
            {
                return Json("""{"MediaContainer":{"totalSize":1,"Metadata":[{"guid":"plex://movie/5d776b"}]}}""");
            }

            return request.RequestUri!.AbsolutePath switch
            {
                "/library/sections" => Json("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie"},{"key":"2","type":"show"}]}}"""),
                "/myplex/account" => Json("""{"MyPlex":{"username":"jampat"}}"""),
                "/library/sections/1/all" => Json(
                    """
                    {"MediaContainer":{"totalSize":1,"Metadata":[
                    {"ratingKey":"101","type":"movie","guid":"plex://movie/5d776b","title":"Sintel","year":2010,"addedAt":1756684800,"viewCount":2,"lastViewedAt":1757530800,"userRating":9,
                     "rating":8.8,"audienceRating":7.2,
                     "Guid":[{"id":"imdb://tt1727587"},{"id":"tmdb://45745"}],
                     "Media":[{"videoResolution":"1080","bitrate":4200,"videoCodec":"h264","Part":[{"file":"/media/Deluno-E2E/Movies/Sintel (2010)/Sintel.mkv"}]}]}]}}
                    """),
                "/library/metadata/101" => Json(
                    """
                    {"MediaContainer":{"Metadata":[{"ratingKey":"101",
                     "Genre":[{"tag":"Animation"}],"Label":[{"tag":"kids"}],"Collection":[{"tag":"Blender films"}],"Role":[{"tag":"Halina Reijn"}],"Director":[{"tag":"Colin Levy"}],
                     "Rating":[{"image":"imdb://image.rating","value":7.4,"type":"audience"},{"image":"rottentomatoes://image.rating.ripe","value":8.8,"type":"critic"},{"image":"rottentomatoes://image.rating.upright","value":7.9,"type":"audience"},{"image":"themoviedb://image.rating","value":7.1,"type":"audience"}]}]}}
                    """),
                "/playlists" => Json("""{"MediaContainer":{"totalSize":1,"Metadata":[{"ratingKey":"900","title":"Friday night"}]}}"""),
                "/playlists/900/items" => Json("""{"MediaContainer":{"totalSize":1,"Metadata":[{"ratingKey":"101"}]}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), "movies", CancellationToken.None);

        var library = Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value;
        Assert.Equal(["jampat"], library.Users);
        var sintel = Assert.Single(library.Items);
        Assert.Equal("101", sintel.ServerItemId);
        Assert.Equal("45745", sintel.TmdbId);
        Assert.Equal("tt1727587", sintel.ImdbId);
        Assert.Equal("1", sintel.SectionId);
        Assert.Equal("/media/Deluno-E2E/Movies/Sintel (2010)/Sintel.mkv", Assert.Single(sintel.Paths));
        Assert.True(sintel.Watch.PlayedByAnyone);
        Assert.False(sintel.Watch.PerUserKnown);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1757530800), sintel.Watch.LastPlayedUtc);
        var viewer = Assert.Single(sintel.Viewers);
        Assert.Equal(("jampat", true, 2, (bool?)false, 9d), (viewer.User, viewer.Played, viewer.PlayCount, viewer.Favourite, viewer.Rating));
        Assert.Equal(new MediaServerRatings(8.8, 7.2, 7.4, 8.8, 7.9, 7.1), sintel.Ratings);
        Assert.Equal(["Halina Reijn", "Colin Levy"], sintel.People);
        Assert.Equal(["Animation"], sintel.Genres);
        Assert.Equal(["kids"], sintel.Labels);
        Assert.Equal(["Blender films"], sintel.Collections);
        Assert.Equal(["Friday night"], sintel.Playlists);
        Assert.Equal(["jampat"], sintel.WatchlistedBy);
        Assert.Equal(new MediaServerVideo("1080p", 4200, "h264"), sintel.Video);
    }

    [Fact]
    public async Task A_Plex_item_rated_the_maximum_is_read_as_a_favourite()
    {
        var client = new PlexMediaServerClient(Factory(request =>
        {
            if (request.RequestUri!.Host == "discover.provider.plex.tv") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return request.RequestUri!.AbsolutePath switch
            {
                "/library/sections" => Json("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie"}]}}"""),
                "/library/sections/1/all" => Json(
                    """{"MediaContainer":{"totalSize":1,"Metadata":[{"ratingKey":"101","type":"movie","title":"Sintel","userRating":10}]}}"""),
                "/library/metadata/101" => Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"101"}]}}"""),
                "/playlists" => Json("""{"MediaContainer":{"totalSize":0}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), "movies", CancellationToken.None);

        var sintel = Assert.Single(Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value.Items);
        var viewer = Assert.Single(sintel.Viewers);
        Assert.True(viewer.Favourite);
    }

    [Fact]
    public async Task A_Plex_item_never_rated_reports_favourite_as_unknown()
    {
        var client = new PlexMediaServerClient(Factory(request =>
        {
            if (request.RequestUri!.Host == "discover.provider.plex.tv") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return request.RequestUri!.AbsolutePath switch
            {
                "/library/sections" => Json("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie"}]}}"""),
                "/library/sections/1/all" => Json(
                    """{"MediaContainer":{"totalSize":1,"Metadata":[{"ratingKey":"101","type":"movie","title":"Sintel","viewCount":1}]}}"""),
                "/library/metadata/101" => Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"101"}]}}"""),
                "/playlists" => Json("""{"MediaContainer":{"totalSize":0}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), "movies", CancellationToken.None);

        var sintel = Assert.Single(Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value.Items);
        var viewer = Assert.Single(sintel.Viewers);
        Assert.Null(viewer.Favourite);
    }

    [Fact]
    public async Task A_Plex_watchlist_that_cannot_be_asked_is_unknown_not_empty()
    {
        var client = new PlexMediaServerClient(Factory(request =>
        {
            if (request.RequestUri!.Host == "discover.provider.plex.tv") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return request.RequestUri!.AbsolutePath switch
            {
                "/library/sections" => Json("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie"}]}}"""),
                "/library/sections/1/all" => Json("""{"MediaContainer":{"totalSize":1,"Metadata":[{"ratingKey":"101","type":"movie","title":"Sintel"}]}}"""),
                "/library/metadata/101" => Json("""{"MediaContainer":{"Metadata":[{"ratingKey":"101"}]}}"""),
                "/playlists" => Json("""{"MediaContainer":{"totalSize":0}}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), "movies", CancellationToken.None);

        var sintel = Assert.Single(Assert.IsType<MediaServerAnswer<MediaServerLibrary>.Reported>(answer).Value.Items);
        Assert.Null(sintel.WatchlistedBy);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static IHttpClientFactory Factory(Func<HttpRequestMessage, HttpResponseMessage> send)
        => new StubFactory(new HttpClient(new StubHandler(send)));

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
