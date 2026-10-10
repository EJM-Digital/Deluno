using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1201 and #1212: removing a download from a usenet client removes it from wherever it is, a client that answers
/// 200 with a "no" is a failure, and a client's "yes" is never believed on its own: the job must be there before the
/// call and gone after it. The SABnzbd answers below are the shapes SABnzbd 5.1.3 gave on the golden run: a history
/// delete answers {"status":true} for any id, a queue delete of an id it lacks answers {"status":false,"nzo_ids":[]}.
/// </summary>
public sealed class RemovingAUsenetDownloadTests
{
    private const string EmptyQueue = "{\"queue\":{\"status\":\"Idle\",\"slots\":[]}}";
    private const string EmptyHistory = "{\"history\":{\"slots\":[]}}";

    private static string QueueWith(string nzoId)
        => "{\"queue\":{\"status\":\"Downloading\",\"slots\":[{\"nzo_id\":\"" + nzoId + "\",\"filename\":\"Some.Film.2020.1080p\",\"status\":\"Downloading\",\"mb\":\"100\",\"mbleft\":\"50\"}]}}";

    private static string HistoryWith(string nzoId, string name, string storage)
        => "{\"history\":{\"slots\":[{\"nzo_id\":\"" + nzoId + "\",\"name\":\"" + name + "\",\"status\":\"Completed\",\"storage\":"
            + System.Text.Json.JsonSerializer.Serialize(storage) + ",\"path\":\"/downloads/incomplete/" + name + "\"}]}}";

    /// <summary>A stand-in SABnzbd: the lists it holds, and a queue delete that removes only what it has.</summary>
    private sealed class FakeSab(string queueJson, string historyJson, bool historyDeleteRemoves = true)
    {
        private string _queue = queueJson;
        private string _history = historyJson;

        public List<string> Asked { get; } = [];

        public HttpResponseMessage Answer(HttpRequestMessage request)
        {
            var query = request.RequestUri!.Query;
            Asked.Add(query);
            if (query.Contains("name=delete", StringComparison.Ordinal))
            {
                if (query.Contains("mode=queue", StringComparison.Ordinal))
                {
                    var had = _queue.Contains("nzo_id", StringComparison.Ordinal);
                    if (had) _queue = EmptyQueue;
                    return Json(had ? "{\"status\":true,\"nzo_ids\":[\"x\"]}" : "{\"status\":false,\"nzo_ids\":[]}");
                }

                // History delete: yes for any id, as SABnzbd 5.1.3 does.
                if (historyDeleteRemoves) _history = EmptyHistory;
                return Json("{\"status\":true}");
            }

            return Json(query.Contains("mode=history", StringComparison.Ordinal) ? _history : _queue);
        }
    }

    [Fact]
    public async Task A_queued_sabnzbd_job_is_removed_from_the_queue_with_its_files()
    {
        var sab = new FakeSab(QueueWith("SABnzbd_nzo_abc"), EmptyHistory);

        var result = await Sab(new StubHandler(sab.Answer)).ExecuteActionAsync(Sabnzbd(), DownloadClientActions.DeleteWithData, "SABnzbd_nzo_abc", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var delete = Assert.Single(sab.Asked, query => query.Contains("name=delete", StringComparison.Ordinal));
        Assert.Contains("mode=queue", delete);
        Assert.Contains("del_files=1", delete);
    }

    [Fact]
    public async Task A_finished_sabnzbd_job_is_removed_from_history_with_its_files()
    {
        var sab = new FakeSab(EmptyQueue, HistoryWith("SABnzbd_nzo_abc", "Some.Film.2020.1080p", Path.Combine(Path.GetTempPath(), "deluno-no-such-folder", "Some.Film.2020.1080p")));

        var result = await Sab(new StubHandler(sab.Answer)).ExecuteActionAsync(Sabnzbd(), DownloadClientActions.DeleteWithData, "SABnzbd_nzo_abc", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("history", result.Message, StringComparison.OrdinalIgnoreCase);
        var history = Assert.Single(sab.Asked, query => query.Contains("mode=history", StringComparison.Ordinal) && query.Contains("name=delete", StringComparison.Ordinal));
        Assert.Contains("value=SABnzbd_nzo_abc", history);
        Assert.Contains("del_files=1", history);
    }

    /// <summary>#1212 (a): the film, the .nfo, Poster.jpg and Sample\ were all still there an hour after Deluno said it deleted its files.</summary>
    [Fact]
    public async Task A_finished_sabnzbd_jobs_completed_folder_goes_with_it_extras_included()
    {
        using var scratch = new ScratchFolder();
        var finished = Path.Combine(scratch.Path, "Completed", "Movies", "Some.Film.2020.1080p");
        Directory.CreateDirectory(Path.Combine(finished, "Sample"));
        File.WriteAllText(Path.Combine(finished, "Some.Film.2020.1080p.mkv"), "film");
        File.WriteAllText(Path.Combine(finished, "Some.Film.2020.1080p.nfo"), "nfo");
        File.WriteAllText(Path.Combine(finished, "Poster.jpg"), "poster");
        File.WriteAllText(Path.Combine(finished, "Sample", "sample.txt"), "sample");
        var sab = new FakeSab(EmptyQueue, HistoryWith("SABnzbd_nzo_abc", "Some.Film.2020.1080p", finished));

        var result = await Sab(new StubHandler(sab.Answer)).ExecuteActionAsync(Sabnzbd(), DownloadClientActions.DeleteWithData, "SABnzbd_nzo_abc", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(Directory.Exists(finished));
        Assert.Null(result.FilesNote);
        Assert.True(Directory.Exists(Path.Combine(scratch.Path, "Completed", "Movies")), "only the job's own folder goes");
    }

    [Fact]
    public async Task A_plain_delete_of_a_finished_sabnzbd_job_leaves_its_folder_alone()
    {
        using var scratch = new ScratchFolder();
        var finished = Path.Combine(scratch.Path, "Some.Film.2020.1080p");
        Directory.CreateDirectory(finished);
        var sab = new FakeSab(EmptyQueue, HistoryWith("SABnzbd_nzo_abc", "Some.Film.2020.1080p", finished));

        var result = await Sab(new StubHandler(sab.Answer)).ExecuteActionAsync(Sabnzbd(), DownloadClientActions.Delete, "SABnzbd_nzo_abc", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(Directory.Exists(finished));
        Assert.Contains(sab.Asked, query => query.Contains("mode=history", StringComparison.Ordinal) && query.Contains("del_files=0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_folder_that_is_not_the_jobs_own_is_left_alone_and_said_so()
    {
        using var scratch = new ScratchFolder();
        var shared = Path.Combine(scratch.Path, "Completed", "Movies");
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "Another.Film.mkv"), "someone else's film");
        var sab = new FakeSab(EmptyQueue, HistoryWith("SABnzbd_nzo_abc", "Some.Film.2020.1080p", shared));

        var result = await Sab(new StubHandler(sab.Answer)).ExecuteActionAsync(Sabnzbd(), DownloadClientActions.DeleteWithData, "SABnzbd_nzo_abc", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(Path.Combine(shared, "Another.Film.mkv")));
        Assert.Contains("not named after this download", result.FilesNote);
        Assert.DoesNotContain("along with its files", result.Message);
    }

    /// <summary>#1212 (b): a removal of an id SABnzbd never had read "deleted its files".</summary>
    [Fact]
    public async Task Removing_an_id_sabnzbd_does_not_have_is_a_failure_and_never_reaches_the_delete()
    {
        var sab = new FakeSab(EmptyQueue, EmptyHistory);

        var result = await Sab(new StubHandler(sab.Answer)).ExecuteActionAsync(Sabnzbd(), DownloadClientActions.DeleteWithData, "SABnzbd_nzo_missing", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("has no download with that id", result.Message);
        Assert.DoesNotContain(sab.Asked, query => query.Contains("name=delete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_removal_of_the_same_sabnzbd_job_fails_instead_of_saying_done_again()
    {
        var sab = new FakeSab(EmptyQueue, HistoryWith("SABnzbd_nzo_abc", "Some.Film.2020.1080p", Path.Combine(Path.GetTempPath(), "deluno-no-such-folder", "x")));
        var client = Sab(new StubHandler(sab.Answer));

        var first = await client.ExecuteActionAsync(Sabnzbd(), DownloadClientActions.DeleteWithData, "SABnzbd_nzo_abc", CancellationToken.None);
        var second = await client.ExecuteActionAsync(Sabnzbd(), DownloadClientActions.DeleteWithData, "SABnzbd_nzo_abc", CancellationToken.None);

        Assert.True(first.Succeeded, first.Message);
        Assert.False(second.Succeeded);
    }

    [Fact]
    public async Task A_yes_from_sabnzbd_does_not_count_while_the_job_is_still_in_its_history()
    {
        var sab = new FakeSab(EmptyQueue, HistoryWith("SABnzbd_nzo_abc", "Some.Film.2020.1080p", Path.Combine(Path.GetTempPath(), "deluno-no-such-folder", "x")), historyDeleteRemoves: false);

        var result = await Sab(new StubHandler(sab.Answer)).ExecuteActionAsync(Sabnzbd(), DownloadClientActions.DeleteWithData, "SABnzbd_nzo_abc", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("still has this download", result.Message);
    }

    [Fact]
    public async Task A_sabnzbd_that_cannot_list_its_jobs_is_a_failure_with_nothing_deleted()
    {
        var asked = new List<string>();
        var handler = new StubHandler(request =>
        {
            asked.Add(request.RequestUri!.Query);
            return request.RequestUri.Query.Contains("name=delete", StringComparison.Ordinal)
                ? Json("{\"status\":true}")
                : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var result = await Sab(handler).ExecuteActionAsync(Sabnzbd(), DownloadClientActions.DeleteWithData, "SABnzbd_nzo_abc", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(asked, query => query.Contains("name=delete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sabnzbd_saying_false_to_a_delete_is_a_failure()
    {
        var handler = new StubHandler(request => request.RequestUri!.Query.Contains("name=delete", StringComparison.Ordinal)
            ? Json("{\"status\":false,\"error\":\"API Key Incorrect\"}")
            : Json(request.RequestUri.Query.Contains("mode=history", StringComparison.Ordinal) ? EmptyHistory : QueueWith("SABnzbd_nzo_abc")));

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

    /// <summary>A stand-in NZBGet: the groups and history it holds, and edits that act on what it holds.</summary>
    private sealed class FakeNzbGet(bool inQueue, bool inHistory)
    {
        public List<string> Bodies { get; } = [];

        public HttpResponseMessage Answer(HttpRequestMessage request)
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            Bodies.Add(body);
            if (body.Contains("\"listgroups\"", StringComparison.Ordinal))
            {
                return Json(inQueue ? "{\"result\":[{\"NZBID\":42,\"NZBName\":\"Some.Film\",\"Status\":\"DOWNLOADING\",\"FileSizeHi\":0,\"RemainingSizeHi\":0}]}" : "{\"result\":[]}");
            }

            if (body.Contains("\"history\"", StringComparison.Ordinal))
            {
                return Json(inHistory ? "{\"result\":[{\"NZBID\":42,\"NZBName\":\"Some.Film\",\"Status\":\"SUCCESS/ALL\",\"FileSizeHi\":0}]}" : "{\"result\":[]}");
            }

            if (body.Contains("\"editqueue\"", StringComparison.Ordinal))
            {
                var group = body.Contains("GroupFinalDelete", StringComparison.Ordinal) || body.Contains("GroupDelete", StringComparison.Ordinal);
                // NZBGet answers true only for an id it matched in the place the command works on.
                var matched = group ? inQueue : inHistory;
                if (matched)
                {
                    if (group) inQueue = false; else inHistory = false;
                }

                return Json(matched ? "{\"result\":true}" : "{\"result\":false}");
            }

            return Json("{\"result\":null}");
        }
    }

    [Fact]
    public async Task Nzbget_removing_an_id_it_does_not_have_is_a_failure()
    {
        var nzb = new FakeNzbGet(inQueue: false, inHistory: false);

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(new StubHandler(nzb.Answer)))
            .ExecuteActionAsync(NzbGet(), DownloadClientActions.DeleteWithData, "42", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("has no download with that id", result.Message);
        Assert.DoesNotContain(nzb.Bodies, body => body.Contains("editqueue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nzbget_that_says_yes_but_still_lists_the_job_has_not_removed_it()
    {
        // An edit that answers true and changes nothing.
        var nzb = new FakeNzbGet(inQueue: true, inHistory: false);
        var handler = new StubHandler(request =>
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return body.Contains("editqueue", StringComparison.Ordinal) ? Json("{\"result\":true}") : nzb.Answer(request);
        });

        // Given its settle window (#1244) and still listing the job at the end of it, the removal did not happen.
        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(handler), removalSettle: TimeSpan.FromMilliseconds(300), removalPoll: TimeSpan.FromMilliseconds(50))
            .ExecuteActionAsync(NzbGet(), DownloadClientActions.DeleteWithData, "42", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("still has this download", result.Message);
    }

    [Fact]
    public async Task A_queued_nzbget_job_is_removed_from_its_queue()
    {
        var nzb = new FakeNzbGet(inQueue: true, inHistory: false);

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(new StubHandler(nzb.Answer)))
            .ExecuteActionAsync(NzbGet(), DownloadClientActions.Delete, "42", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task Nzbget_answering_with_an_error_is_a_failure_with_its_words()
    {
        var nzb = new FakeNzbGet(inQueue: true, inHistory: false);
        var handler = new StubHandler(request =>
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            return body.Contains("editqueue", StringComparison.Ordinal)
                ? Json("{\"result\":null,\"error\":{\"code\":1,\"message\":\"Access denied\"}}")
                : nzb.Answer(request);
        });

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(handler))
            .ExecuteActionAsync(NzbGet(), DownloadClientActions.DeleteWithData, "42", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Access denied", result.Message);
    }

    [Fact]
    public async Task A_finished_nzbget_job_is_removed_from_history_when_the_queue_has_none()
    {
        var nzb = new FakeNzbGet(inQueue: false, inHistory: true);

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(new StubHandler(nzb.Answer)))
            .ExecuteActionAsync(NzbGet(), DownloadClientActions.DeleteWithData, "42", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains(nzb.Bodies, body => body.Contains("GroupFinalDelete", StringComparison.Ordinal));
        Assert.Contains(nzb.Bodies, body => body.Contains("HistoryFinalDelete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_nzbget_that_cannot_be_reached_is_a_failure_not_an_exception()
    {
        var handler = new ThrowingHandler();

        var result = await new NzbGetDownloadClient(new StubHttpClientFactory(handler))
            .ExecuteActionAsync(NzbGet(), DownloadClientActions.Pause, "42", CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    private sealed class ScratchFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deluno-sab-" + Guid.NewGuid().ToString("n"));

        public ScratchFolder() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
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
