using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.MediaServers;
using Deluno.Integrations.Seerr;

namespace Deluno.Integrations.Tests.Seerr;

/// <summary>
/// #522: Jellyseerr and Overseerr, against a fake that answers the way Overseerr
/// does - and "could not ask" never mistaken for "nobody requested it".
/// </summary>
public sealed class SeerrClientTests
{
    private static SeerrConnection Server()
        => new("seerr-1", "Lab Seerr", "http://192.168.1.51:5055", "key-123", true, "untested", null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task A_test_proves_the_key_and_says_which_server_and_version()
    {
        var client = new SeerrClient(Factory(request =>
        {
            Assert.Equal("key-123", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v1/status" => Json("""{"version":"2.7.3","commitTag":"local"}"""),
                "/api/v1/settings/main" => Json("""{"applicationTitle":"Overseerr lab"}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));

        var result = await client.TestAsync(Server(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(("Overseerr lab", "2.7.3"), (result.ServerName, result.Version));
        Assert.Equal("Connected to Overseerr lab (version 2.7.3).", result.Message);
    }

    [Fact]
    public async Task A_refused_key_is_not_healthy()
    {
        var client = new SeerrClient(Factory(request => request.RequestUri!.AbsolutePath == "/api/v1/status"
            ? Json("""{"version":"2.7.3"}""")
            : new HttpResponseMessage(HttpStatusCode.Forbidden)));

        var result = await client.TestAsync(Server(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("refused the token", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requests_are_read_across_pages_with_who_asked_and_when_approved()
    {
        var pages = new List<string>();
        var client = new SeerrClient(Factory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            if (path == "/api/v1/request")
            {
                pages.Add(query["skip"]!);
                if (query["skip"] == "0")
                {
                    var many = string.Join(",", Enumerable.Range(0, 100).Select(i =>
                        "{\"id\":" + i + ",\"status\":1,\"createdAt\":\"2026-09-01T00:00:00.000Z\",\"updatedAt\":\"2026-09-01T00:00:00.000Z\",\"media\":{\"id\":" + (1000 + i) + ",\"tmdbId\":" + (5000 + i) + ",\"mediaType\":\"movie\"},\"requestedBy\":{\"displayName\":\"filler\"}}"));
                    return Json($$"""{"pageInfo":{"pages":2,"results":101,"page":1},"results":[{{many}}]}""");
                }

                return Json("""
                    {"pageInfo":{"pages":2,"results":101,"page":2},"results":[
                      {"id":500,"status":2,"createdAt":"2026-08-01T10:00:00.000Z","updatedAt":"2026-08-02T09:00:00.000Z","media":{"id":77,"tmdbId":653,"tvdbId":null,"mediaType":"movie"},"requestedBy":{"displayName":"Ben","jellyfinUsername":"ben"}}
                    ]}
                    """);
            }

            if (path == "/api/v1/user") return Json("""{"pageInfo":{"pages":1,"results":2},"results":[{"displayName":"Ben"},{"jellyfinUsername":"sam"}]}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var answer = await client.ListRequestsAsync(Server(), CancellationToken.None);

        var library = Assert.IsType<MediaServerAnswer<SeerrLibrary>.Reported>(answer).Value;
        Assert.Equal(["0", "100"], pages);
        Assert.Equal(101, library.Requests.Count);
        var nosferatu = Assert.Single(library.Requests, request => request.TmdbId == "653");
        Assert.Equal((77, "movies", "Ben"), (nosferatu.MediaId, nosferatu.MediaType, nosferatu.RequestedBy));
        Assert.Equal(DateTimeOffset.Parse("2026-08-02T09:00:00Z"), nosferatu.ApprovedUtc);
        // Pending is not approved.
        Assert.Null(library.Requests[0].ApprovedUtc);
        Assert.Equal(["Ben", "sam"], library.Users);
    }

    [Fact]
    public async Task A_request_listing_that_breaks_part_way_is_not_an_answer()
    {
        var client = new SeerrClient(Factory(request =>
        {
            var skip = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["skip"];
            return skip == "0"
                ? Json("{\"pageInfo\":{\"results\":150},\"results\":[" + string.Join(",", Enumerable.Range(0, 100).Select(i => "{\"id\":" + i + ",\"status\":1,\"createdAt\":\"2026-09-01T00:00:00Z\",\"media\":{\"id\":" + i + ",\"tmdbId\":" + i + ",\"mediaType\":\"movie\"},\"requestedBy\":{\"displayName\":\"x\"}}")) + "]}")
                : new HttpResponseMessage(HttpStatusCode.BadGateway);
        }));

        Assert.IsType<MediaServerAnswer<SeerrLibrary>.Unreachable>(await client.ListRequestsAsync(Server(), CancellationToken.None));
    }

    [Fact]
    public async Task Clearing_a_film_deletes_its_media_record_found_by_TMDb_id()
    {
        var deleted = new List<string>();
        var client = new SeerrClient(Factory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/v1/movie/653") return Json("""{"id":653,"title":"Nosferatu","mediaInfo":{"id":77,"status":5}}""");
            if (request.Method == HttpMethod.Delete && path.StartsWith("/api/v1/media/", StringComparison.Ordinal))
            {
                deleted.Add(path);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var answer = await client.ClearAsync(Server(), "movies", "653", null, CancellationToken.None);

        Assert.True(Assert.IsType<MediaServerAnswer<SeerrClearing>.Reported>(answer).Value.Cleared);
        Assert.Equal(["/api/v1/media/77"], deleted);
    }

    [Fact]
    public async Task A_title_Seerr_never_knew_has_nothing_to_clear_and_a_show_is_found_by_TVDb_id()
    {
        var deleted = new List<string>();
        var client = new SeerrClient(Factory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v1/movie/999") return Json("""{"id":999,"title":"Never requested"}""");
            if (path == "/api/v1/media") return Json("""{"pageInfo":{"results":2},"results":[{"id":3,"tvdbId":81189,"mediaType":"tv"},{"id":4,"tvdbId":1,"mediaType":"movie"}]}""");
            if (request.Method == HttpMethod.Delete)
            {
                deleted.Add(path);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var film = await client.ClearAsync(Server(), "movies", "999", null, CancellationToken.None);
        var show = await client.ClearAsync(Server(), "tv", null, "81189", CancellationToken.None);

        Assert.False(Assert.IsType<MediaServerAnswer<SeerrClearing>.Reported>(film).Value.Cleared);
        Assert.True(Assert.IsType<MediaServerAnswer<SeerrClearing>.Reported>(show).Value.Cleared);
        Assert.Equal(["/api/v1/media/3"], deleted);
    }

    [Fact]
    public async Task A_Seerr_that_is_down_could_not_be_cleared()
    {
        var client = new SeerrClient(Factory(_ => throw new HttpRequestException("refused")));

        var answer = await client.ClearAsync(Server(), "movies", "653", null, CancellationToken.None);

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
