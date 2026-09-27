using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// A grab to NZBGet uses the method NZBGet takes, and NZBGet's answer decides
/// whether it was sent (#633).
///
/// <para><b>Why this file exists.</b> On the rig, six grabs to NZBGet 26.3
/// were recorded as sent and none arrived. Deluno called <c>appendurl</c>;
/// NZBGet answered with an error inside an HTTP 200, and Deluno never read the
/// answer.</para>
/// </summary>
public sealed class NzbGetGrabIsCheckedTests
{
    private const string RefusedAsOnTheRig = """
        {"version":"1.1","error":{"name":"JSONRPCError","code":2,"message":"Invalid parameter (DupeKey)"}}
        """;

    [Fact]
    public async Task A_grab_NZBGet_refuses_is_a_failed_grab_that_says_why()
    {
        var sent = new List<string>();

        var result = await Grab(RefusedAsOnTheRig, sent);

        Assert.False(result.Succeeded);
        Assert.Contains("Invalid parameter (DupeKey)", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_grab_NZBGet_adds_is_sent_with_its_NZBID()
    {
        var sent = new List<string>();

        var result = await Grab("""{"version":"1.1","result":42}""", sent);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("42", result.ExternalId);
        var body = Assert.Single(sent);
        Assert.Contains("\"method\":\"append\"", body, StringComparison.Ordinal);
        Assert.Contains("\"Pioneer.One.S01E01.1080p.nzb\"", body, StringComparison.Ordinal);
        Assert.Contains("\"https://indexer.invalid/get/1\"", body, StringComparison.Ordinal);
        Assert.Contains("\"SCORE\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_answer_with_no_NZBID_is_not_taken_as_sent()
    {
        var result = await Grab("""{"version":"1.1","result":0}""", []);

        Assert.False(result.Succeeded);
    }

    private static Task<DownloadClientGrabResult> Grab(string reply, List<string> sent)
        => new NzbGetDownloadClient(new StubFactory(new FakeNzbGet(reply, sent))).GrabAsync(
            new DownloadClientItem(
                "client-1", "NZBGet", "nzbget", "localhost", 6789, "nzbget", "secret", "http://localhost:6789/", false, null,
                "Movies", "TV", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            new DownloadClientGrabRequest("Pioneer.One.S01E01.1080p", "https://indexer.invalid/get/1", "tv", "TV", "altHUB"),
            CancellationToken.None);

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeNzbGet(string reply, List<string> sent) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            sent.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }
}
