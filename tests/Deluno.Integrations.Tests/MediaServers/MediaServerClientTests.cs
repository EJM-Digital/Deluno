using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.MediaServers;

namespace Deluno.Integrations.Tests.MediaServers;

/// <summary>
/// #493: Plex, Jellyfin and Emby, read the way each one actually answers - and
/// "could not ask" never mistaken for "nothing there".
/// </summary>
public sealed class MediaServerClientTests
{
    private static MediaServerConnection Server(string kind, string baseUrl = "http://192.168.1.51:8096")
        => new("server-1", kind, "Rig " + kind, baseUrl, "token-123", true, "untested", null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Jellyfin_combines_what_every_user_has_watched()
    {
        var requests = new List<string>();
        var client = new JellyfinMediaServerClient(Factory(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            // Jellyfin 12 refuses the legacy X-Emby-Token header (#493, found on the rig).
            Assert.Equal("MediaBrowser Token=\"token-123\"", Assert.Single(request.Headers.GetValues("Authorization")));
            Assert.False(request.Headers.Contains("X-Emby-Token"));
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;
            return path switch
            {
                "/Users" => Json("""[{"Id":"u1","Name":"James"},{"Id":"u2","Name":"Ben"},{"Id":"u3","Name":"Old","Policy":{"IsDisabled":true}}]"""),
                "/Items" => Json(
                    """
                    {"Items":[
                    {"Id":"m1","Name":"Sintel","ProductionYear":2010,"ProviderIds":{"Tmdb":"45745","Imdb":"tt1727587"},"Path":"\\\\nas\\media\\Deluno-E2E\\Movies\\Sintel (2010)\\Sintel.mkv","DateCreated":"2026-09-01T00:00:00Z"},
                    {"Id":"m2","Name":"Big Buck Bunny","ProductionYear":2008,"ProviderIds":{"tmdb":"10378"},"Path":"/media/bbb.mkv"}],"TotalRecordCount":2}
                    """),
                "/Users/u1/Items" => Json("""{"Items":[{"Id":"m1","UserData":{"Played":true,"LastPlayedDate":"2026-09-10T20:00:00Z"}}],"TotalRecordCount":1}"""),
                "/Users/u2/Items" => Json("""{"Items":[{"Id":"m1","UserData":{"Played":true,"LastPlayedDate":"2026-09-12T21:00:00Z"}}],"TotalRecordCount":1}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Jellyfin), "movies", CancellationToken.None);

        var items = Assert.IsType<MediaServerAnswer<IReadOnlyList<MediaServerItem>>.Reported>(answer).Value;
        var sintel = Assert.Single(items, item => item.ServerItemId == "m1");
        Assert.Equal("45745", sintel.TmdbId);
        Assert.Equal("tt1727587", sintel.ImdbId);
        Assert.True(sintel.Watch.PlayedByAnyone);
        Assert.True(sintel.Watch.PerUserKnown);
        Assert.Equal(["James", "Ben"], sintel.Watch.PlayedBy);
        Assert.Equal(DateTimeOffset.Parse("2026-09-12T21:00:00Z"), sintel.Watch.LastPlayedUtc);
        var bunny = Assert.Single(items, item => item.ServerItemId == "m2");
        Assert.Equal("10378", bunny.TmdbId);
        Assert.False(bunny.Watch.PlayedByAnyone);
        Assert.DoesNotContain(requests, request => request.StartsWith("/Users/u3/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_empty_library_is_an_answer()
    {
        var client = new JellyfinMediaServerClient(Factory(request => request.RequestUri!.AbsolutePath switch
        {
            "/Users" => Json("""[{"Id":"u1","Name":"James"}]"""),
            _ => Json("""{"Items":[],"TotalRecordCount":0}""")
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Jellyfin), "movies", CancellationToken.None);

        Assert.Empty(Assert.IsType<MediaServerAnswer<IReadOnlyList<MediaServerItem>>.Reported>(answer).Value);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_server_that_refuses_or_fails_could_not_be_asked(HttpStatusCode code)
    {
        var client = new EmbyMediaServerClient(Factory(_ => new HttpResponseMessage(code)));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Emby), "movies", CancellationToken.None);

        Assert.IsType<MediaServerAnswer<IReadOnlyList<MediaServerItem>>.Unreachable>(answer);
    }

    [Fact]
    public async Task A_server_that_is_down_could_not_be_asked()
    {
        var client = new PlexMediaServerClient(Factory(_ => throw new HttpRequestException("connection refused")));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), "movies", CancellationToken.None);

        var unreachable = Assert.IsType<MediaServerAnswer<IReadOnlyList<MediaServerItem>>.Unreachable>(answer);
        Assert.Contains("could not reach", unreachable.Reason, StringComparison.Ordinal);
    }

    /// <summary>A second page that fails must fail the whole answer, not hand back the first page as if it were everything.</summary>
    [Fact]
    public async Task A_listing_that_breaks_part_way_could_not_be_asked()
    {
        var calls = 0;
        var client = new JellyfinMediaServerClient(Factory(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/Users") return Json("""[{"Id":"u1","Name":"James"}]""");
            if (request.RequestUri.AbsolutePath != "/Items") return Json("""{"Items":[],"TotalRecordCount":0}""");
            if (calls++ > 0) return new HttpResponseMessage(HttpStatusCode.BadGateway);
            var page = string.Join(",", Enumerable.Range(0, 500).Select(i => $$"""{"Id":"m{{i}}","Name":"Film {{i}}"}"""));
            return Json($$"""{"Items":[{{page}}],"TotalRecordCount":900}""");
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Jellyfin), "movies", CancellationToken.None);

        Assert.IsType<MediaServerAnswer<IReadOnlyList<MediaServerItem>>.Unreachable>(answer);
    }

    [Fact]
    public async Task Emby_is_asked_under_its_emby_path_and_says_who_it_is()
    {
        var paths = new List<string>();
        var client = new EmbyMediaServerClient(Factory(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath switch
            {
                "/emby/System/Info" => Json("""{"ServerName":"RIG-EMBY","Id":"abc123","Version":"4.9.1.80"}"""),
                "/emby/Library/VirtualFolders" => Json("""[{"Name":"Movies"},{"Name":"TV"}]"""),
                "/emby/Users" => Json("""[{"Id":"u1","Name":"James"}]"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));

        var result = await client.TestAsync(Server(MediaServerKinds.Emby, "http://192.168.1.51:8097"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("RIG-EMBY", result.ServerName);
        Assert.Equal("abc123", result.ServerId);
        Assert.Equal(2, result.LibraryCount);
        Assert.Contains("RIG-EMBY", result.Message, StringComparison.Ordinal);
        Assert.All(paths, path => Assert.StartsWith("/emby/", path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Removing_from_Jellyfin_reports_the_files_deleted_and_never_calls_its_delete()
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        var client = new JellyfinMediaServerClient(Factory(request =>
        {
            sent = request;
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        var item = new MediaServerItem("m1", "movies", "Sintel", 2010, null, null, null, [@"\\nas\media\Deluno-E2E\Movies\Sintel (2010)\Sintel.mkv"], null, MediaServerWatchState.Unplayed);

        var answer = await client.RemoveAsync(Server(MediaServerKinds.Jellyfin), item, CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerRemoval>.Reported>(answer);
        Assert.Equal(HttpMethod.Post, sent!.Method);
        Assert.Equal("/Library/Media/Updated", sent.RequestUri!.AbsolutePath);
        Assert.Contains("\"UpdateType\":\"Deleted\"", body, StringComparison.Ordinal);
        Assert.Contains("Sintel.mkv", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plex_reads_ids_files_and_one_account_watch_state()
    {
        var client = new PlexMediaServerClient(Factory(request =>
        {
            Assert.Equal("token-123", Assert.Single(request.Headers.GetValues("X-Plex-Token")));
            return request.RequestUri!.AbsolutePath switch
            {
                "/library/sections" => Json("""{"MediaContainer":{"Directory":[{"key":"1","type":"movie"},{"key":"2","type":"show"}]}}"""),
                "/library/sections/1/all" => Json(
                    """
                    {"MediaContainer":{"totalSize":1,"Metadata":[
                    {"ratingKey":"101","title":"Sintel","year":2010,"addedAt":1756684800,"viewCount":2,"lastViewedAt":1757530800,
                     "Guid":[{"id":"imdb://tt1727587"},{"id":"tmdb://45745"}],
                     "Media":[{"Part":[{"file":"/media/Deluno-E2E/Movies/Sintel (2010)/Sintel.mkv"}]}]}]}}
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));

        var answer = await client.ListItemsAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), "movies", CancellationToken.None);

        var sintel = Assert.Single(Assert.IsType<MediaServerAnswer<IReadOnlyList<MediaServerItem>>.Reported>(answer).Value);
        Assert.Equal("101", sintel.ServerItemId);
        Assert.Equal("45745", sintel.TmdbId);
        Assert.Equal("tt1727587", sintel.ImdbId);
        Assert.Equal("1", sintel.SectionId);
        Assert.Equal("/media/Deluno-E2E/Movies/Sintel (2010)/Sintel.mkv", Assert.Single(sintel.Paths));
        Assert.True(sintel.Watch.PlayedByAnyone);
        Assert.False(sintel.Watch.PerUserKnown);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1757530800), sintel.Watch.LastPlayedUtc);
    }

    [Fact]
    public async Task Removing_from_Plex_rescans_the_folder_and_empties_trash_and_never_deletes()
    {
        var sent = new List<(HttpMethod Method, string PathAndQuery)>();
        var client = new PlexMediaServerClient(Factory(request =>
        {
            sent.Add((request.Method, Uri.UnescapeDataString(request.RequestUri!.PathAndQuery)));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var item = new MediaServerItem("101", "movies", "Sintel", 2010, null, null, null, ["/media/Movies/Sintel (2010)/Sintel.mkv"], null, MediaServerWatchState.Unplayed, "1");

        var answer = await client.RemoveAsync(Server(MediaServerKinds.Plex, "http://192.168.1.51:32400"), item, CancellationToken.None);

        Assert.IsType<MediaServerAnswer<MediaServerRemoval>.Reported>(answer);
        Assert.Equal((HttpMethod.Get, "/library/sections/1/refresh?path=/media/Movies/Sintel (2010)"), sent[0]);
        Assert.Equal((HttpMethod.Put, "/library/sections/1/emptyTrash"), sent[1]);
        Assert.DoesNotContain(sent, call => call.Method == HttpMethod.Delete);
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
