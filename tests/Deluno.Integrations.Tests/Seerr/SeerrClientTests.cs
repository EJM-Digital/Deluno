using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Integrations.Seerr;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.Seerr;

/// <summary>
/// #522: Jellyseerr and Overseerr, against the replies of the real app (Seerr 3.5.0, recorded in Docker: tests/fixtures/recorded/seerr, #1237) -
/// and "could not ask" never mistaken for "nobody requested it". A test that needs a state the recording does not hold (a second page of
/// requests, a show known by its TVDb id) takes the nearest recorded reply and changes only the value it is about, and says so.
/// </summary>
public sealed class SeerrClientTests
{
    private static SeerrConnection Server()
        => new("seerr-1", "Lab Seerr", "http://192.168.1.51:5055", "key-123", true, "untested", null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static RecordedReply Reply(string state, int exchange) => RecordedReplies.Seerr(state).Exchanges[exchange].Reply;

    [Fact]
    public async Task A_test_proves_the_key_and_says_which_server_and_version()
    {
        var client = new SeerrClient(Factory(request =>
        {
            Assert.Equal("key-123", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/status" => Reply("status", 0).ToResponse(),
                "/api/v1/settings/main" => Reply("settings-main", 0).ToResponse(),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));

        var result = await client.TestAsync(Server(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(("Seerr", "3.5.0"), (result.ServerName, result.Version));
        Assert.Equal("Connected to Seerr (version 3.5.0).", result.Message);
    }

    [Fact]
    public async Task A_refused_key_is_not_healthy()
    {
        var client = new SeerrClient(Factory(request => request.RequestUri!.AbsolutePath == "/api/v1/status"
            ? Reply("status", 0).ToResponse()
            : Reply("wrong-key", 0).ToResponse()));

        var result = await client.TestAsync(Server(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("refused the token", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requests_are_read_across_pages_with_who_asked_and_when_approved()
    {
        var pages = new List<string>();
        // The first page is the recorded list of one pending request copied up to a hundred (the page size); the second is the recorded list of one
        // approved request. The page info says there are 101.
        var first = Reply("request-pending", 1).BodyWith(("+results", 100), ("pageInfo.results", 101));
        var client = new SeerrClient(Factory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            if (path == "/api/v1/request")
            {
                pages.Add(query["skip"]!);
                return query["skip"] == "0" ? Json(first) : Json(Reply("request-approved", 1).BodyWith(("pageInfo.results", 101)));
            }

            return path == "/api/v1/user" ? Reply("request-pending", 2).ToResponse() : new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var answer = await client.ListRequestsAsync(Server(), CancellationToken.None);

        var library = Assert.IsType<MediaServerAnswer<SeerrLibrary>.Reported>(answer).Value;
        Assert.Equal(["0", "100"], pages);
        Assert.Equal(101, library.Requests.Count);
        var approved = Assert.Single(library.Requests, request => request.ApprovedUtc is not null);
        Assert.Equal(("movies", "45745"), (approved.MediaType, approved.TmdbId));
        Assert.Equal(approved.RequestedBy, library.Requests[0].RequestedBy);
        // Pending is not approved.
        Assert.Null(library.Requests[0].ApprovedUtc);
        Assert.NotEmpty(library.Users);
    }

    [Fact]
    public async Task A_request_listing_that_breaks_part_way_is_not_an_answer()
    {
        var first = Reply("request-pending", 1).BodyWith(("+results", 100), ("pageInfo.results", 150));
        var client = new SeerrClient(Factory(request =>
        {
            var skip = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["skip"];
            return skip == "0" ? Json(first) : new HttpResponseMessage(HttpStatusCode.BadGateway);
        }));

        Assert.IsType<MediaServerAnswer<SeerrLibrary>.Unreachable>(await client.ListRequestsAsync(Server(), CancellationToken.None));
    }

    [Fact]
    public async Task Clearing_a_film_deletes_its_media_record_found_by_TMDb_id()
    {
        var deleted = new List<string>();
        var lookup = Reply("request-pending", 3);
        var mediaId = (int)lookup.Json()["mediaInfo"]!["id"]!;
        var client = new SeerrClient(Factory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/movie/45745") return lookup.ToResponse();
            if (request.Method == HttpMethod.Delete && path.StartsWith("/api/v1/media/", StringComparison.Ordinal))
            {
                deleted.Add(path);
                return Reply("media-deleted", 0).ToResponse();
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var answer = await client.ClearAsync(Server(), "movies", "45745", null, CancellationToken.None);

        Assert.True(Assert.IsType<MediaServerAnswer<SeerrClearing>.Reported>(answer).Value.Cleared);
        Assert.Equal([$"/api/v1/media/{mediaId}"], deleted);
    }

    [Fact]
    public async Task A_title_Seerr_never_knew_has_nothing_to_clear_and_a_show_is_found_by_TVDb_id()
    {
        var deleted = new List<string>();
        // The recorded media list (one film) with its record made a series known by the TVDb id the show carries; the recorded delete.
        var media = Reply("request-pending", 4).BodyWith(("results.0.mediaType", "tv"), ("results.0.tvdbId", 81189), ("results.0.id", 3));
        var client = new SeerrClient(Factory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/movie/45745") return Reply("lookup-unknown", 0).ToResponse();
            if (path == "/api/v1/media") return Json(media);
            if (request.Method == HttpMethod.Delete)
            {
                deleted.Add(path);
                return Reply("media-deleted", 0).ToResponse();
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var film = await client.ClearAsync(Server(), "movies", "45745", null, CancellationToken.None);
        var show = await client.ClearAsync(Server(), "tv", null, "81189", CancellationToken.None);

        Assert.False(Assert.IsType<MediaServerAnswer<SeerrClearing>.Reported>(film).Value.Cleared);
        Assert.True(Assert.IsType<MediaServerAnswer<SeerrClearing>.Reported>(show).Value.Cleared);
        Assert.Equal(["/api/v1/media/3"], deleted);
    }

    [Fact]
    public async Task A_Seerr_that_is_down_could_not_be_cleared()
    {
        var client = new SeerrClient(new StubFactory(new HttpClient(RecordedReplies.Seerr("not-answering").NotAnswering())));

        var answer = await client.ClearAsync(Server(), "movies", "45745", null, CancellationToken.None);

        Assert.Equal("Deluno could not reach Lab Seerr.", Assert.IsType<MediaServerAnswer<SeerrClearing>.Unreachable>(answer).Reason);
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
