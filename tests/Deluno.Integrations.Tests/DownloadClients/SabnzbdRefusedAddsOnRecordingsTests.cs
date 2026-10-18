using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// What SABnzbd 5.1.3 really answers when an add is refused or goes wrong, recorded from the image the end-to-end stack pins, in Docker
/// (tests/fixtures/recorded/sabnzbd, #1237). Deluno adds with <c>mode=addurl</c>: a refusal is <c>{"status": false}</c> (with an error text for
/// a missing link), a wrong key is HTTP 403 with a plain text body, and a link that fails after SABnzbd took the job fails in its history.
/// </summary>
public sealed class SabnzbdRefusedAddsOnRecordingsTests
{
    private static DownloadClientItem Client()
        => new("sab-1", "SABnzbd", "sabnzbd", "localhost", 8085, null, "api-key", "http://localhost:8085/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static DownloadClientGrabRequest Request(string url)
        => new("Recorded.Refused.Add", url, "movies", "movies", "Fixture indexer");

    private static Task<DownloadClientGrabResult> Grab(string state, int exchange, string url)
    {
        var reply = RecordedReplies.Sabnzbd(state).Exchanges[exchange].Reply;
        var handler = RecordedReplies.Handler("sabnzbd", [], (request, _) => request.RequestUri!.Query.Contains("mode=addurl", StringComparison.Ordinal) ? reply.ToResponse() : null);
        return new SabnzbdDownloadClient(new Factory(handler)).GrabAsync(Client(), Request(url), CancellationToken.None);
    }

    [Fact]
    public async Task A_link_SABnzbd_reads_as_no_link_at_all_is_refused_with_its_error_and_the_release_is_read_as_dead()
    {
        var recorded = RecordedReplies.Sabnzbd("add-no-url").Exchanges[0].Reply;
        Assert.Equal("expects one parameter", (string?)recorded.Json()["error"]);

        var result = await Grab("add-no-url", 0, "");

        Assert.False(result.Succeeded);
        Assert.Equal(DownloadQueueStatuses.DeadRelease, result.Status);
        Assert.Contains("did not accept the release URL", result.Message, StringComparison.Ordinal);
        Assert.Contains("expects one parameter", result.ResponseJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_text_that_is_not_a_link_is_refused_with_status_false_and_no_job_and_the_release_is_read_as_dead()
    {
        var recorded = RecordedReplies.Sabnzbd("add-no-url");
        Assert.False((bool)recorded.Exchanges[1].Reply.Json()["status"]!);
        Assert.Empty(recorded.Exchanges[1].Reply.Json()["nzo_ids"]!.AsArray());
        Assert.Empty(recorded.Exchanges[2].Reply.Json()["queue"]!["slots"]!.AsArray());
        Assert.Empty(recorded.Exchanges[3].Reply.Json()["history"]!["slots"]!.AsArray());

        var result = await Grab("add-no-url", 1, "this is not a link");

        Assert.False(result.Succeeded);
        Assert.Equal(DownloadQueueStatuses.DeadRelease, result.Status);
        Assert.Equal(200, result.ResponseCode);
    }

    [Fact]
    public async Task A_wrong_key_on_an_add_is_HTTP_403_with_a_plain_text_body_and_never_a_dead_release()
    {
        var recorded = RecordedReplies.Sabnzbd("add-wrong-key");
        Assert.Equal(403, recorded.Exchanges[0].Reply.Status);
        Assert.Equal(403, recorded.Exchanges[1].Reply.Status);
        Assert.Contains("API Key Incorrect", recorded.Exchanges[0].Reply.Body, StringComparison.Ordinal);

        var result = await Grab("add-wrong-key", 0, "http://indexer.test/getnzb/abc123.nzb");

        Assert.False(result.Succeeded);
        Assert.NotEqual(DownloadQueueStatuses.DeadRelease, result.Status);
        Assert.True(result.Message.Contains("API key", StringComparison.OrdinalIgnoreCase), result.Message);
    }

    [Fact]
    public async Task A_dead_link_is_taken_as_a_job_and_fails_afterwards_in_the_history_with_SABnzbds_reason()
    {
        var recorded = RecordedReplies.Sabnzbd("add-url-dead");

        var grab = await Grab("add-url-dead", 0, "http://indexer.test/dl/Recorded.Nothing.Here.nzb");
        var handler = RecordedReplies.Handler("sabnzbd", [recorded, RecordedReplies.Sabnzbd("version"), RecordedReplies.Sabnzbd("settings"), RecordedReplies.Sabnzbd("idle")]);
        var snapshot = await new SabnzbdDownloadClient(new Factory(handler)).GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(grab.Succeeded, grab.Message);
        Assert.Equal((string?)recorded.Exchanges[0].Reply.Json()["nzo_ids"]![0], grab.ExternalId, ignoreCase: true);
        var failed = Assert.Single(snapshot!.History, item => item.ReleaseName.StartsWith("Recorded.Dead.Link", StringComparison.Ordinal));
        Assert.Equal("failed", failed.Outcome);
        Assert.Contains("File not on server", failed.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_link_that_answers_something_that_is_not_an_nzb_is_taken_as_a_job_too_and_waits_in_the_queue_as_grabbing_before_it_fails()
    {
        var recorded = RecordedReplies.Sabnzbd("add-url-dead");

        var grab = await Grab("add-url-dead", 1, "http://indexer.test/dl/not-an-nzb");
        var handler = RecordedReplies.Handler("sabnzbd", [recorded, RecordedReplies.Sabnzbd("version"), RecordedReplies.Sabnzbd("settings"), RecordedReplies.Sabnzbd("idle")]);
        var snapshot = await new SabnzbdDownloadClient(new Factory(handler)).GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(grab.Succeeded, grab.Message);
        Assert.Equal("Grabbing", (string?)recorded.Exchanges[2].Reply.Json()["queue"]!["slots"]![0]!["status"]);
        Assert.Contains(snapshot!.Queue, item => item.ReleaseName.StartsWith("Recorded.Not.An", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Queue, item => item.Status == DownloadQueueStatuses.ImportReady);
    }
}
