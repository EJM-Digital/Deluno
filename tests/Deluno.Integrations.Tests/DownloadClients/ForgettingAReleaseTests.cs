using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// Forgetting a release, which is not the same as deleting it.
///
/// <para>A torrent client refuses a release because it still holds the
/// infohash, so removing the transfer and its data is the whole of forgetting
/// it. A usenet client refuses it from <em>history</em>, and history survives
/// the queue being emptied — which is precisely the trap James described:
/// "radarr and sabnzbd etc keep that history and prevent the title from being
/// downloaded again but doesn't really tell the user why or how to fix it".</para>
///
/// <para>So on SABnzbd and NZBGet a forget is two requests, and the second one
/// is the one that matters. Before this, the override asked for
/// <c>delete-with-data</c>, SABnzbd answered "unsupported", and a forced
/// re-download against a usenet client reported success and changed
/// nothing — a confident failure, which is worse than none.</para>
/// </summary>
public sealed class ForgettingAReleaseTests
{
    [Fact]
    public async Task Forgetting_on_sabnzbd_clears_the_history_and_not_only_the_queue()
    {
        var asked = new List<string>();
        var handler = new StubHandler(request =>
        {
            asked.Add(request.RequestUri?.Query ?? string.Empty);
            return JsonResponse("{\"status\":true}");
        });

        var result = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler))
            .ExecuteActionAsync(Sabnzbd(), DownloadClientActions.Forget, "SABnzbd_nzo_abc123", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains(asked, query => query.Contains("mode=queue", StringComparison.OrdinalIgnoreCase)
                                        && query.Contains("name=delete", StringComparison.OrdinalIgnoreCase));
        // The half that actually lets the release back in.
        Assert.Contains(asked, query => query.Contains("mode=history", StringComparison.OrdinalIgnoreCase)
                                        && query.Contains("name=delete", StringComparison.OrdinalIgnoreCase));
        Assert.All(asked, query => Assert.Contains("SABnzbd_nzo_abc123", query, StringComparison.Ordinal));
    }

    /// <summary>
    /// The commonest shape of the problem: the download finished long ago, the
    /// file was deleted, and all that is left is the history entry.
    /// </summary>
    [Fact]
    public async Task Forgetting_succeeds_when_only_the_history_had_it()
    {
        var handler = new StubHandler(request =>
            request.RequestUri!.Query.Contains("mode=history", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse("{\"status\":true}")
                : JsonResponse("{\"status\":false}"));

        var result = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler))
            .ExecuteActionAsync(Sabnzbd(), DownloadClientActions.Forget, "SABnzbd_nzo_abc123", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("history", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And a client that cannot be reached says so rather than reporting a
    /// cleanup it never performed.
    /// </summary>
    [Fact]
    public async Task Forgetting_reports_failure_when_sabnzbd_cannot_be_reached()
    {
        var handler = new ThrowingHandler();

        var result = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler))
            .ExecuteActionAsync(Sabnzbd(), DownloadClientActions.Forget, "SABnzbd_nzo_abc123", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("could not be reached", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// `delete-with-data` was simply missing from SABnzbd's verb list, so the
    /// override's request fell through to "unsupported" and the person was told
    /// their force had worked.
    /// </summary>
    [Fact]
    public async Task Deleting_with_data_is_no_longer_unsupported_on_sabnzbd()
    {
        string? asked = null;
        var handler = new StubHandler(request =>
        {
            asked = request.RequestUri?.Query;
            return JsonResponse("{\"status\":true}");
        });

        var result = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler))
            .ExecuteActionAsync(Sabnzbd(), DownloadClientActions.DeleteWithData, "SABnzbd_nzo_abc123", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("del_files=1", asked, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// NZBGet keeps the same thing in the same place, and only
    /// <c>HistoryFinalDelete</c> removes it — <c>HistoryDelete</c> hides it,
    /// and a hidden entry still refuses the release.
    /// </summary>
    [Fact]
    public async Task Forgetting_on_nzbget_finally_deletes_the_history_entry()
    {
        var bodies = new List<string>();
        var handler = new StubHandler(request =>
        {
            bodies.Add(request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty);
            return JsonResponse("{\"result\":true}");
        });

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(handler))
            .ExecuteActionAsync(NzbGet(), DownloadClientActions.Forget, "42", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        // GroupFinalDelete rather than GroupDelete: forgetting takes the files
        // too, and leaving them has the client refuse the release again for the
        // same reason.
        Assert.Contains(bodies, body => body.Contains("GroupFinalDelete", StringComparison.Ordinal));
        Assert.Contains(bodies, body => body.Contains("HistoryFinalDelete", StringComparison.Ordinal));
    }

    /// <summary>
    /// "all" is not a download id. To SABnzbd it means the whole history.
    ///
    /// <para>Deluno only ever passes an id it read from its own dispatch
    /// record, so this cannot happen today. It is guarded because the cost of
    /// being wrong once is somebody's entire download history, and the check is
    /// one comparison.</para>
    /// </summary>
    [Theory]
    [InlineData("all")]
    [InlineData("failed")]
    [InlineData("completed")]
    public async Task Forgetting_refuses_a_value_sabnzbd_would_read_as_the_whole_history(string selector)
    {
        var sent = 0;
        var handler = new StubHandler(_ =>
        {
            sent++;
            return JsonResponse("{\"status\":true}");
        });

        var result = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler))
            .ExecuteActionAsync(Sabnzbd(), DownloadClientActions.Forget, selector, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, sent);
        Assert.Contains("whole history", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A torrent client needs no second request: it refuses by infohash, so the
    /// transfer going away is the whole of forgetting. What matters is that the
    /// files go with it — leaving them has the client refuse the same release
    /// again for the same reason.
    /// </summary>
    [Fact]
    public async Task Forgetting_on_qbittorrent_removes_the_transfer_with_its_files()
    {
        var bodies = new List<string>();
        var handler = new StubHandler(request =>
        {
            bodies.Add(request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
        });

        var result = await new QbittorrentDownloadClient(() => handler)
            .ExecuteActionAsync(Qbittorrent(), DownloadClientActions.Forget, "hash-1", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains(bodies, body => body.Contains("deleteFiles=true", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ helpers

    private static DownloadClientItem Sabnzbd() => Client("sabnzbd", "SABnzbd", "api-key");

    private static DownloadClientItem NzbGet() => Client("nzbget", "NZBGet", null);

    private static DownloadClientItem Qbittorrent() => Client("qbittorrent", "qBittorrent", null);

    private static DownloadClientItem Client(string protocol, string name, string? secret)
        => new(
            "client-1",
            name,
            protocol,
            "localhost",
            8080,
            null,
            secret,
            "http://localhost:8080/",
            "movies",
            "tv",
            null,
            1,
            true,
            "healthy",
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("The client is not answering.");
    }
}
