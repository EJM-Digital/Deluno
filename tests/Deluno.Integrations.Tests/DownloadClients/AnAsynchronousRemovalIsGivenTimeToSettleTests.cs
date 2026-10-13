using System.Diagnostics;
using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1244, from the rc.30.20 golden run (Stuckfilm): the stall strike told NZBGet to remove a download stuck at 99.9%
/// and read "NZBGet still has this download after being told to remove it", although NZBGet removed the job a minute
/// later. NZBGet's <c>GroupDelete</c> answers true at once and takes the job out of its queue asynchronously: a job with
/// a connection still waiting on an article is listed until that connection gives up. #1212's look after the call was
/// made straight away, so it saw the job and called the removal failed.
///
/// <para>The stand-in below lists the job for the next few looks after the delete and then drops it, which is the
/// golden run's behaviour as the record gives it. Every reply it gives is NZBGet 26.3's own, recorded (#1237).</para>
/// </summary>
public sealed class AnAsynchronousRemovalIsGivenTimeToSettleTests
{
    /// <summary>
    /// A stand-in NZBGet that answers from NZBGet 26.3's recorded replies (#1237): the job is the recorded downloading film (id 26) or
    /// the recorded finished one in the history, a removal is answered as the recording answers it, and the lists keep the job for a few
    /// looks after the delete and then show the recorded lists of a client that no longer has it.
    /// </summary>
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
                return RecordedReplies.Nzbget("removed-without-data").Response("editqueue");
            }

            if (body.Contains("\"listgroups\"", StringComparison.Ordinal))
            {
                if (_deleteSeen)
                {
                    ListLooksAfterDelete++;
                    if (_stillListedFor-- <= 0) _inQueue = false;
                }

                return _inQueue ? RecordedReplies.Nzbget("downloading").Response("listgroups") : RecordedReplies.Nzbget("removed-without-data").Response("listgroups");
            }

            if (body.Contains("\"history\"", StringComparison.Ordinal))
            {
                if (_deleteSeen && _inHistory && historyLooksLeft-- <= 0) _inHistory = false;
                return _inHistory ? RecordedReplies.Nzbget("finished-single-file").Response("history\"") : RecordedReplies.Nzbget("removed-with-data").Response("history\"");
            }

            throw new InvalidOperationException("NZBGet was asked something the recordings do not answer: " + body);
        }
    }

    [Theory]
    [InlineData(DownloadClientActions.Delete)]
    [InlineData(DownloadClientActions.DeleteWithData)]
    public async Task A_queued_nzbget_job_still_listed_straight_after_the_delete_but_gone_a_moment_later_is_removed(string action)
    {
        var nzb = new SlowNzbGet(lookingLeftAfterDelete: 2);

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(new StubHandler(nzb.Answer)))
            .ExecuteActionAsync(NzbGet(), action, "26", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.DoesNotContain("still has this download", result.Message);
        Assert.True(nzb.ListLooksAfterDelete >= 3, "NZBGet was asked again until it let the job go");
    }

    [Fact]
    public async Task A_removal_that_does_not_settle_within_the_window_is_reported_as_not_removed_with_the_wait_said()
    {
        var nzb = new SlowNzbGet(lookingLeftAfterDelete: int.MaxValue);

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(new StubHandler(nzb.Answer)), removalSettle: TimeSpan.FromMilliseconds(400), removalPoll: TimeSpan.FromMilliseconds(50))
            .ExecuteActionAsync(NzbGet(), DownloadClientActions.Delete, "26", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("still has this download", result.Message);
        Assert.True(nzb.ListLooksAfterDelete > 2, "it was looked at more than once while waiting");
    }

    [Fact]
    public async Task A_finished_nzbget_job_leaving_the_history_a_moment_after_the_delete_is_removed()
    {
        var nzb = new SlowNzbGet(lookingLeftAfterDelete: 0, queued: false, historyLooksLeft: 2);

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(new StubHandler(nzb.Answer)))
            .ExecuteActionAsync(NzbGet(), DownloadClientActions.DeleteWithData, "26", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task A_removal_that_settles_at_once_is_not_made_to_wait()
    {
        var nzb = new SlowNzbGet(lookingLeftAfterDelete: 0);
        var timer = Stopwatch.StartNew();

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(new StubHandler(nzb.Answer)))
            .ExecuteActionAsync(NzbGet(), DownloadClientActions.Delete, "26", CancellationToken.None);

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
                ? RecordedReplies.Json(RecordedReplies.Nzbget("rpc-error").Body("nosuchmethod", ("error.message", "Access denied")))
                : nzb.Answer(request);
        });
        var timer = Stopwatch.StartNew();

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(handler))
            .ExecuteActionAsync(NzbGet(), DownloadClientActions.DeleteWithData, "26", CancellationToken.None);

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
            // A client that has no job with that id: its recorded lists, which hold other jobs, and the recorded reply to an edit of an id it never had.
            return body.Contains("\"editqueue\"", StringComparison.Ordinal) ? RecordedReplies.Nzbget("unknown-id").Response("editqueue")
                : body.Contains("\"listgroups\"", StringComparison.Ordinal) ? RecordedReplies.Nzbget("idle").Response("listgroups")
                : RecordedReplies.Nzbget("idle").Response("history\"");
        });

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(handler))
            .ExecuteActionAsync(NzbGet(), DownloadClientActions.Delete, "26", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("has no download with that id", result.Message);
    }

    /// <summary>SABnzbd's history delete is immediate, so its removal is still judged on the first look after the call.</summary>
    [Fact]
    public async Task A_sabnzbd_that_still_lists_the_job_straight_after_a_removal_fails_without_waiting()
    {
        // SABnzbd 5.1.3's recorded history, which holds the finished film still after the delete, as the stand-in's SABnzbd keeps it.
        const string jobId = "7690dfab-c8ad-4a5e-92cd-427a14ce4ea6";
        var asked = new List<string>();
        var handler = new StubHandler(request =>
        {
            var query = request.RequestUri!.Query;
            asked.Add(query);
            if (query.Contains("name=delete", StringComparison.Ordinal))
            {
                return RecordedReplies.Sabnzbd("removed-with-data").Response("name=delete");
            }

            return query.Contains("mode=history", StringComparison.Ordinal)
                ? RecordedReplies.Sabnzbd("finished-single-file").Response("mode=history")
                : RecordedReplies.Sabnzbd("idle").Response("mode=queue");
        });
        var timer = Stopwatch.StartNew();

        var result = await new SabnzbdDownloadClient(new StubHttpClientFactory(handler))
            .ExecuteActionAsync(Client("sabnzbd", "SABnzbd", "api-key"), DownloadClientActions.DeleteWithData, jobId, CancellationToken.None);

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
