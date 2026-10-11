using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1000: SABnzbd's <c>addurl</c> gets the release's own name as
/// <c>nzbname</c> ("Name of the job, if empty the NZB filename is used"),
/// distinct from the URL still sent as <c>name</c>. SABnzbd's reply is its recorded one (#1237).
/// </summary>
public sealed class SabnzbdNzbNameTests
{
    private const string Release = "Big.Buck.Bunny.2008.1080p.WEB-DL.x264-DELUNO";

    [Fact]
    public async Task Addurl_sends_the_release_name_as_nzbname_beside_the_url_as_name()
    {
        var queries = new List<string>();
        var handler = new StubHandler(request =>
        {
            queries.Add(request.RequestUri!.Query);
            return RecordedReplies.Sabnzbd("queued").Response("mode=addurl");
        });

        var result = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler))
            .GrabAsync(SabClient(), NzbGrab(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var query = Assert.Single(queries);
        Assert.Contains("mode=addurl", query, StringComparison.Ordinal);
        Assert.Contains($"nzbname={Uri.EscapeDataString(Release)}", query, StringComparison.Ordinal);
        Assert.Contains($"name={Uri.EscapeDataString("http://indexer.test/getnzb/abc123.nzb")}", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Addurl_sends_no_nzbname_when_the_release_has_no_name()
    {
        var queries = new List<string>();
        var handler = new StubHandler(request =>
        {
            queries.Add(request.RequestUri!.Query);
            return RecordedReplies.Sabnzbd("queued").Response("mode=addurl");
        });

        await new SabnzbdDownloadClient(new StubHttpClientFactory(handler))
            .GrabAsync(SabClient(), NzbGrab() with { ReleaseName = "  " }, CancellationToken.None);

        Assert.DoesNotContain("nzbname", Assert.Single(queries), StringComparison.Ordinal);
    }

    private static DownloadClientGrabRequest NzbGrab()
        => new(Release, "http://indexer.test/getnzb/abc123.nzb", "movies", "movies", "Fixture indexer");

    private static DownloadClientItem SabClient()
        => new(
            Id: "sab-1", Name: "SABnzbd", Protocol: "sabnzbd", Host: "localhost", Port: 8085,
            Username: null, Secret: "api-key", EndpointUrl: "http://localhost:8085/", UseSsl: false, UrlBase: null,
            MoviesCategory: "movies", TvCategory: "tv", CategoryTemplate: null, Priority: 1, IsEnabled: true,
            HealthStatus: "healthy", LastHealthMessage: null, LastHealthFailureCategory: null, LastHealthLatencyMs: null,
            LastHealthTestUtc: null, CreatedUtc: DateTimeOffset.UtcNow, UpdatedUtc: DateTimeOffset.UtcNow);

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
