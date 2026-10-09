using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1201: removing a download from a usenet client removes it from wherever it
/// is, and a client that answers 200 with a "no" is a failure, not a success.
/// </summary>
public sealed class RemovingAUsenetDownloadTests
{
    [Fact]
    public async Task A_queued_sabnzbd_job_is_removed_from_the_queue_with_its_files()
    {
        var asked = new List<string>();
        var handler = new StubHandler(request =>
        {
            asked.Add(request.RequestUri!.Query);
            return Json("{\"status\":true,\"nzo_ids\":[\"SABnzbd_nzo_abc\"]}");
        });

        var result = await Sab(handler).ExecuteActionAsync(Sabnzbd(), DownloadClientActions.DeleteWithData, "SABnzbd_nzo_abc", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var only = Assert.Single(asked);
        Assert.Contains("mode=queue", only);
        Assert.Contains("name=delete", only);
        Assert.Contains("del_files=1", only);
    }

    [Fact]
    public async Task A_finished_sabnzbd_job_is_removed_from_history_with_its_files()
    {
        var asked = new List<string>();
        var handler = new StubHandler(request =>
        {
            var query = request.RequestUri!.Query;
            asked.Add(query);
            // The queue no longer has it: SABnzbd says yes and removes nothing.
            return query.Contains("mode=queue", StringComparison.Ordinal)
                ? Json("{\"status\":true,\"nzo_ids\":[]}")
                : Json("{\"status\":true}");
        });

        var result = await Sab(handler).ExecuteActionAsync(Sabnzbd(), DownloadClientActions.DeleteWithData, "SABnzbd_nzo_abc", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("history", result.Message, StringComparison.OrdinalIgnoreCase);
        var history = Assert.Single(asked, query => query.Contains("mode=history", StringComparison.Ordinal));
        Assert.Contains("name=delete", history);
        Assert.Contains("value=SABnzbd_nzo_abc", history);
        Assert.Contains("del_files=1", history);
    }

    [Fact]
    public async Task A_plain_delete_of_a_finished_sabnzbd_job_leaves_its_files()
    {
        var asked = new List<string>();
        var handler = new StubHandler(request =>
        {
            asked.Add(request.RequestUri!.Query);
            return request.RequestUri.Query.Contains("mode=queue", StringComparison.Ordinal)
                ? Json("{\"status\":false}")
                : Json("{\"status\":true}");
        });

        var result = await Sab(handler).ExecuteActionAsync(Sabnzbd(), DownloadClientActions.Delete, "SABnzbd_nzo_abc", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains(asked, query => query.Contains("mode=history", StringComparison.Ordinal) && query.Contains("del_files=0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sabnzbd_saying_false_to_a_delete_is_a_failure()
    {
        var handler = new StubHandler(_ => Json("{\"status\":false,\"error\":\"API Key Incorrect\"}"));

        var result = await Sab(handler).ExecuteActionAsync(Sabnzbd(), DownloadClientActions.DeleteWithData, "SABnzbd_nzo_abc", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain("action sent", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("API Key Incorrect", result.Message);
    }

    [Theory]
    [InlineData(DownloadClientActions.Pause)]
    [InlineData(DownloadClientActions.Resume)]
    [InlineData(DownloadClientActions.MoveToTop)]
    public async Task Sabnzbd_saying_false_to_any_other_action_is_a_failure(string action)
    {
        var handler = new StubHandler(_ => Json("{\"status\":false,\"error\":\"Unknown job\"}"));

        var result = await Sab(handler).ExecuteActionAsync(Sabnzbd(), action, "SABnzbd_nzo_abc", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Unknown job", result.Message);
    }

    [Fact]
    public async Task Sabnzbd_saying_yes_to_a_pause_is_still_a_success()
    {
        var handler = new StubHandler(_ => Json("{\"status\":true}"));

        var result = await Sab(handler).ExecuteActionAsync(Sabnzbd(), DownloadClientActions.Pause, "SABnzbd_nzo_abc", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task Nzbget_saying_false_to_an_edit_is_a_failure()
    {
        var handler = new StubHandler(_ => Json("{\"result\":false}"));

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(handler))
            .ExecuteActionAsync(NzbGet(), DownloadClientActions.Pause, "42", CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Nzbget_answering_with_an_error_is_a_failure_with_its_words()
    {
        var handler = new StubHandler(_ => Json("{\"result\":null,\"error\":{\"code\":1,\"message\":\"Access denied\"}}"));

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(handler))
            .ExecuteActionAsync(NzbGet(), DownloadClientActions.DeleteWithData, "42", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Access denied", result.Message);
    }

    [Fact]
    public async Task A_finished_nzbget_job_is_removed_from_history_when_the_queue_has_none()
    {
        var bodies = new List<string>();
        var handler = new StubHandler(request =>
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            bodies.Add(body);
            return Json(body.Contains("HistoryFinalDelete", StringComparison.Ordinal) ? "{\"result\":true}" : "{\"result\":false}");
        });

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(handler))
            .ExecuteActionAsync(NzbGet(), DownloadClientActions.DeleteWithData, "42", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains(bodies, body => body.Contains("GroupFinalDelete", StringComparison.Ordinal));
        Assert.Contains(bodies, body => body.Contains("HistoryFinalDelete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_nzbget_that_cannot_be_reached_is_a_failure_not_an_exception()
    {
        var handler = new ThrowingHandler();

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(handler))
            .ExecuteActionAsync(NzbGet(), DownloadClientActions.Pause, "42", CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    private static SabnzbdDownloadClient Sab(HttpMessageHandler handler) => new(new StubHttpClientFactory(handler));

    private static DownloadClientItem Sabnzbd() => Client("sabnzbd", "SABnzbd", "api-key");

    private static DownloadClientItem NzbGet() => Client("nzbget", "NZBGet", null);

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
            false,
            null,
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

    private static HttpResponseMessage Json(string body)
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
