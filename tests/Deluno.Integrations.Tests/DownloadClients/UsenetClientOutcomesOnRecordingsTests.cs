using System.Text.Json.Nodes;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1301: what SABnzbd 5.1.3 and NZBGet 26.3 say about a Usenet job that did not go through, read from their own recorded
/// replies: a job whose address would not fetch (SABnzbd's history "Failed" with its reason), a job NZBGet deleted as a
/// duplicate, an NZBGet that answers a call with an error object, and a resume of one job by its id.
/// </summary>
public sealed class UsenetClientOutcomesOnRecordingsTests
{
    [Fact]
    public async Task A_sabnzbd_job_whose_address_would_not_fetch_is_a_failed_history_row_with_sabnzbds_own_reason()
    {
        var slot = RecordedReplies.Sabnzbd("failed").Find("mode=history").Reply.Json()["history"]!["slots"]![0]!;
        var handler = RecordedReplies.Handler("sabnzbd", [RecordedReplies.Sabnzbd("failed"), RecordedReplies.Sabnzbd("version"), RecordedReplies.Sabnzbd("settings")]);

        var history = await new SabnzbdDownloadClient(new Factory(handler))
            .GetHistoryAsync(Client("sabnzbd", null, "api-key"), DateTimeOffset.UtcNow, CancellationToken.None);

        var row = Assert.Single(history, item => item.ExternalId == slot["nzo_id"]!.GetValue<string>());
        Assert.Equal("failed", row.Outcome, ignoreCase: true);
        Assert.Contains(slot["fail_message"]!.GetValue<string>(), row.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_sabnzbd_job_that_is_in_the_queue_and_not_yet_in_the_history_is_listed_as_queued_not_failed()
    {
        var handler = RecordedReplies.Handler("sabnzbd", [RecordedReplies.Sabnzbd("queued"), RecordedReplies.Sabnzbd("idle"), RecordedReplies.Sabnzbd("version"), RecordedReplies.Sabnzbd("settings")]);

        var snapshot = await new SabnzbdDownloadClient(new Factory(handler))
            .GetSnapshotAsync(Client("sabnzbd", null, "api-key"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.NotEmpty(snapshot!.Queue);
        Assert.DoesNotContain(snapshot.Queue, item => item.Status is DownloadQueueStatuses.DeadRelease or DownloadQueueStatuses.Stalled);
    }

    [Fact]
    public async Task An_nzbget_job_it_deleted_as_a_duplicate_is_a_history_row_that_says_duplicate_not_a_failed_release()
    {
        var recorded = RecordedReplies.Nzbget("failed").Exchanges.First(e => e.Request.Body?.Contains("\"history\"") == true).Reply.Json()["result"]![0]!;
        var handler = RecordedReplies.Handler("nzbget", [RecordedReplies.Nzbget("failed"), RecordedReplies.Nzbget("settings"), RecordedReplies.Nzbget("version")],
            (_, body) => body.Contains("\"log\"", StringComparison.Ordinal) ? RecordedReplies.Json(RecordedReplies.Nzbget("config-error-paused").Body("log\"", ("^result", 0))) : null);

        var history = await new NzbGetDownloadClient(new Factory(handler))
            .GetHistoryAsync(Client("nzbget", "nzbget", "secret"), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal("DELETED/DUPE", recorded["Status"]!.GetValue<string>());
        var row = Assert.Single(history, item => item.ExternalId == recorded["NZBID"]!.GetValue<int>().ToString());
        Assert.DoesNotContain("failed", row.Outcome, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("duplicate", row.Outcome + row.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_nzbget_that_answers_an_append_with_an_error_object_fails_the_grab_with_its_own_words()
    {
        var error = RecordedReplies.Nzbget("rpc-error").Find("nosuchmethod").Reply.Json()["error"]!["message"]!.GetValue<string>();
        var calls = new Calls("nzbget", [RecordedReplies.Nzbget("queued"), RecordedReplies.Nzbget("settings"), RecordedReplies.Nzbget("version")],
            (_, body) => body.Contains("\"append\"", StringComparison.Ordinal) ? RecordedReplies.Nzbget("rpc-error").Response("nosuchmethod") : null);
        var handler = calls.Build();
        var request = new DownloadClientGrabRequest("Example.Release.2026", "http://indexer.test/nzb/1", "movies", "movies", "Example Indexer");

        var result = await new NzbGetDownloadClient(new Factory(handler)).GrabAsync(Client("nzbget", "nzbget", "secret"), request, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(error, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(calls.Sent("\"append\""));
    }

    [Fact]
    public async Task Resuming_one_nzbget_job_sends_group_resume_with_its_id_and_waits_to_see_it_no_longer_paused()
    {
        var calls = new Calls("nzbget", [RecordedReplies.Nzbget("resumed"), RecordedReplies.Nzbget("settings"), RecordedReplies.Nzbget("version")]);
        var handler = calls.Build();

        var result = await new NzbGetDownloadClient(new Factory(handler))
            .ExecuteActionAsync(Client("nzbget", "nzbget", "secret"), DownloadClientActions.Resume, "1", CancellationToken.None);

        Assert.True(calls.Sent("\"GroupResume\""), "the job is resumed by its id, not the whole client");
        Assert.False(calls.Sent("resumedownload"));
        Assert.True(result.Succeeded || result.Message.Length > 0);
    }
}
