using System.Net;
using System.Text;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// A client that has been paused as a whole (SABnzbd's queue.paused, NZBGet's
/// DownloadPaused, PostPaused and ScanPaused) is resumed by a client-level call,
/// not by the per-item resume. Only those two clients report such a pause, so
/// every other client says it does not support it.
/// </summary>
public sealed class ResumingAWholeClientTests
{
    [Fact]
    public async Task Sabnzbd_is_resumed_with_mode_resume()
    {
        var handler = new Recorder(_ => Json("""{"status": true}"""));

        var result = await new SabnzbdDownloadClient(new Factory(handler)).ResumeClientAsync(Sab(), CancellationToken.None);

        Assert.True(result.Supported);
        Assert.True(result.Succeeded);
        var call = Assert.Single(handler.Calls);
        Assert.Contains("mode=resume", call.Query);
        Assert.DoesNotContain("value=", call.Query);
        Assert.Contains("apikey=api-key", call.Query);
    }

    [Fact]
    public async Task Sabnzbd_that_says_no_gives_its_reason()
    {
        var handler = new Recorder(_ => Json("""{"status": false, "error": "not allowed"}"""));

        var result = await new SabnzbdDownloadClient(new Factory(handler)).ResumeClientAsync(Sab(), CancellationToken.None);

        Assert.True(result.Supported);
        Assert.False(result.Succeeded);
        Assert.Contains("not allowed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sabnzbd_with_no_api_key_is_not_asked()
    {
        var handler = new Recorder(_ => Json("{}"));

        var result = await new SabnzbdDownloadClient(new Factory(handler)).ResumeClientAsync(Sab() with { Secret = null }, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Nzbget_releases_all_three_of_its_pauses()
    {
        var handler = new Recorder(_ => Json("""{"result": true}"""));

        var result = await new NzbGetDownloadClient(new Factory(handler)).ResumeClientAsync(NzbGet(), CancellationToken.None);

        Assert.True(result.Supported);
        Assert.True(result.Succeeded);
        Assert.Equal(["resumedownload", "resumepost", "resumescan"], handler.Calls.Select(call => Method(call.Body)));
    }

    [Fact]
    public async Task Nzbget_that_refuses_gives_its_reason_and_stops()
    {
        var handler = new Recorder(_ => Json("""{"result": null, "error": {"code": 1, "message": "Access denied"}}"""));

        var result = await new NzbGetDownloadClient(new Factory(handler)).ResumeClientAsync(NzbGet(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Access denied", result.Message, StringComparison.Ordinal);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Clients_with_no_whole_client_pause_say_so()
    {
        var transmission = await new TransmissionDownloadClient(new Factory(new Recorder(_ => Json("{}")))).ResumeClientAsync(Sab(), CancellationToken.None);
        var qbittorrent = await new QbittorrentDownloadClient().ResumeClientAsync(Sab(), CancellationToken.None);

        Assert.False(transmission.Supported);
        Assert.False(qbittorrent.Supported);
        Assert.False(transmission.Succeeded);
        Assert.False(qbittorrent.Succeeded);
    }

    private static string Method(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("method").GetString()!;
    }

    private static DownloadClientItem NzbGet()
        => new("client-1", "NZBGet", "nzbget", "localhost", 6789, "nzbget", "secret", "http://localhost:6789/", false, null,
            "Movies", "TV", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static DownloadClientItem Sab()
        => new("client-1", "SABnzbd", "sabnzbd", "localhost", 8080, null, "api-key", "http://localhost:8080/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Call(string Query, string Body);

    private sealed class Recorder(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new Call(request.RequestUri?.Query ?? string.Empty, body));
            return answer(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
