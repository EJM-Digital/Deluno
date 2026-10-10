using System.Diagnostics;
using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1244, from the rc.30.20 golden run (Stuckfilm): the stall strike told NZBGet to remove a download stuck at 99.9%
/// and read "NZBGet still has this download after being told to remove it", although NZBGet removed the job a minute
/// later. NZBGet's <c>GroupDelete</c> answers true at once and takes the job out of its queue asynchronously: a job with
/// a connection still waiting on an article is listed until that connection gives up. #1212's look after the call was
/// made straight away, so it saw the job and called the removal failed.
///
/// <para>The stand-in below lists the job for the next few looks after the delete and then drops it, which is the
/// golden run's behaviour as the record gives it. The golden run's logs hold no recorded NZBGet replies, so the list
/// shapes are the ones the other tests in this folder use.</para>
/// </summary>
public sealed class AnAsynchronousRemovalIsGivenTimeToSettleTests
{
    /// <summary>A stand-in NZBGet whose edit answers true and whose lists keep the job for a few looks.</summary>
    private sealed class SlowNzbGet(int lookingLeftAfterDelete, bool queued = true, int historyLooksLeft = 0)
    {
        private bool _inQueue = queued;
        private bool _inHistory = !queued;
        private int _stillListedFor;
        private bool _deleteSeen;

        public List<string> Bodies { get; } = [];

        public int ListLooksAfterDelete { get; private set; }

        public HttpResponseMessage Answer(HttpRequestMessage request)
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            Bodies.Add(body);
            if (body.Contains("\"editqueue\"", StringComparison.Ordinal))
            {
                _deleteSeen = true;
                _stillListedFor = lookingLeftAfterDelete;
                return Json("{\"result\":true}");
            }

            if (body.Contains("\"listgroups\"", StringComparison.Ordinal))
            {
                if (_deleteSeen)
                {
                    ListLooksAfterDelete++;
                    if (_stillListedFor-- <= 0) _inQueue = false;
                }

                return Json(_inQueue ? "{\"result\":[{\"NZBID\":42,\"NZBName\":\"Stuckfilm\",\"Status\":\"DOWNLOADING\",\"FileSizeHi\":0,\"RemainingSizeHi\":0}]}" : "{\"result\":[]}");
            }

            if (body.Contains("\"history\"", StringComparison.Ordinal))
            {
                if (_deleteSeen && _inHistory && historyLooksLeft-- <= 0) _inHistory = false;
                return Json(_inHistory ? "{\"result\":[{\"NZBID\":42,\"NZBName\":\"Stuckfilm\",\"Status\":\"SUCCESS/ALL\",\"FileSizeHi\":0}]}" : "{\"result\":[]}");
            }

            return Json("{\"result\":null}");
        }
    }

    [Theory]
    [InlineData(DownloadClientActions.Delete)]
    [InlineData(DownloadClientActions.DeleteWithData)]
    public async Task A_queued_nzbget_job_still_listed_straight_after_the_delete_but_gone_a_moment_later_is_removed(string action)
    {
        var nzb = new SlowNzbGet(lookingLeftAfterDelete: 2);

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(new StubHandler(nzb.Answer)))
            .ExecuteActionAsync(NzbGet(), action, "42", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.DoesNotContain("still has this download", result.Message);
        Assert.True(nzb.ListLooksAfterDelete >= 3, "NZBGet was asked again until it let the job go");
    }

    [Fact]
    public async Task A_removal_that_does_not_settle_within_the_window_is_reported_as_not_removed_with_the_wait_said()
    {
        var nzb = new SlowNzbGet(lookingLeftAfterDelete: int.MaxValue);

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(new StubHandler(nzb.Answer)), removalSettle: TimeSpan.FromMilliseconds(400), removalPoll: TimeSpan.FromMilliseconds(50))
            .ExecuteActionAsync(NzbGet(), DownloadClientActions.Delete, "42", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("still has this download", result.Message);
        Assert.True(nzb.ListLooksAfterDelete > 2, "it was looked at more than once while waiting");
    }

    [Fact]
    public async Task A_finished_nzbget_job_leaving_the_history_a_moment_after_the_delete_is_removed()
    {
        var nzb = new SlowNzbGet(lookingLeftAfterDelete: 0, queued: false, historyLooksLeft: 2);

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(new StubHandler(nzb.Answer)))
            .ExecuteActionAsync(NzbGet(), DownloadClientActions.DeleteWithData, "42", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task A_removal_that_settles_at_once_is_not_made_to_wait()
    {
        var nzb = new SlowNzbGet(lookingLeftAfterDelete: 0);
        var timer = Stopwatch.StartNew();

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(new StubHandler(nzb.Answer)))
            .ExecuteActionAsync(NzbGet(), DownloadClientActions.Delete, "42", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1), $"took {timer.Elapsed}");
        Assert.Equal(1, nzb.ListLooksAfterDelete);
    }

    [Fact]
    public async Task An_nzbget_that_refuses_the_removal_fails_at_once_without_waiting()
    {
        var nzb = new SlowNzbGet(lookingLeftAfterDelete: 2);
        var handler = new StubHandler(request =>
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return body.Contains("editqueue", StringComparison.Ordinal)
                ? Json("{\"result\":null,\"error\":{\"code\":1,\"message\":\"Access denied\"}}")
                : nzb.Answer(request);
        });
        var timer = Stopwatch.StartNew();

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(handler))
            .ExecuteActionAsync(NzbGet(), DownloadClientActions.DeleteWithData, "42", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Access denied", result.Message);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1), $"took {timer.Elapsed}");
    }

    [Fact]
    public async Task An_unknown_nzbget_id_still_fails_before_any_delete_is_sent()
    {
        var handler = new StubHandler(request =>
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return Json(body.Contains("\"editqueue\"", StringComparison.Ordinal) ? "{\"result\":true}" : "{\"result\":[]}");
        });

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(handler))
            .ExecuteActionAsync(NzbGet(), DownloadClientActions.Delete, "42", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("has no download with that id", result.Message);
    }

    /// <summary>SABnzbd's history delete is immediate, so its removal is still judged on the first look after the call.</summary>
    [Fact]
    public async Task A_sabnzbd_that_still_lists_the_job_straight_after_a_removal_fails_without_waiting()
    {
        const string history = "{\"history\":{\"slots\":[{\"nzo_id\":\"SABnzbd_nzo_abc\",\"name\":\"Stuckfilm\",\"status\":\"Completed\",\"storage\":\"/nowhere/Stuckfilm\",\"path\":\"/nowhere/Stuckfilm\"}]}}";
        var asked = new List<string>();
        var handler = new StubHandler(request =>
        {
            var query = request.RequestUri!.Query;
            asked.Add(query);
            if (query.Contains("name=delete", StringComparison.Ordinal))
            {
                return Json("{\"status\":true}");
            }

            return Json(query.Contains("mode=history", StringComparison.Ordinal) ? history : "{\"queue\":{\"status\":\"Idle\",\"slots\":[]}}");
        });
        var timer = Stopwatch.StartNew();

        var result = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler))
            .ExecuteActionAsync(Client("sabnzbd", "SABnzbd", "api-key"), DownloadClientActions.DeleteWithData, "SABnzbd_nzo_abc", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("still has this download", result.Message);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1), $"took {timer.Elapsed}");
    }

    private static DownloadClientItem NzbGet() => Client("nzbget", "NZBGet", null);

    private static DownloadClientItem Client(string protocol, string name, string? secret)
        => new(
            "client-1", name, protocol, "localhost", 8080, null, secret, "http://localhost:8080/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

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
}
