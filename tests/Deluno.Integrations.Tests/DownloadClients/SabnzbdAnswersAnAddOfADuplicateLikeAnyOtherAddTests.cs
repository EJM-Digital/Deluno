using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #877 and #1237, from SABnzbd 5.1.3 with its duplicate check switched on (no_dupes) and the same NZB added a second time: it
/// answers the add with a job id like any other add, and decides afterwards, once it has fetched the NZB, what to do with the
/// duplicate. So the grab is a normal send carrying that id, never a refusal and never a duplicate known at once; the later look
/// (the appearance lookup, tested with the history) is what finds a duplicate.
/// </summary>
public sealed class SabnzbdAnswersAnAddOfADuplicateLikeAnyOtherAddTests
{
    private static DownloadClientItem Client()
        => new("sab-1", "SABnzbd", "sabnzbd", "localhost", 8085, null, "api-key", "http://localhost:8085/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task The_second_add_of_the_same_nzb_is_sent_and_carries_the_job_id_sabnzbd_gave()
    {
        var handler = RecordedReplies.Handler("sabnzbd", [RecordedReplies.Sabnzbd("duplicate-add")], (request, _) =>
            request.RequestUri!.Query.Contains("mode=addurl", StringComparison.Ordinal) ? RecordedReplies.Sabnzbd("duplicate-add").Response("mode=addurl") : null);
        var request = new DownloadClientGrabRequest("Recorded.Queued.A", "http://indexer.test/getnzb/abc123.nzb", "movies", "movies", "Fixture indexer");

        var result = await new SabnzbdDownloadClient(new Factory(handler)).GrabAsync(Client(), request, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("31e09c4b-1f7c-48e4-89af-134f24b23eb1", result.ExternalId);
        Assert.False(result.IsDuplicateFollow);
    }

    [Fact]
    public async Task The_queue_after_the_second_add_is_read_with_every_job_in_it()
    {
        var handler = RecordedReplies.Handler(
            "sabnzbd",
            [RecordedReplies.Sabnzbd("duplicate-add"), RecordedReplies.Sabnzbd("idle"), RecordedReplies.Sabnzbd("version"), RecordedReplies.Sabnzbd("settings")]);

        var snapshot = await new SabnzbdDownloadClient(new Factory(handler)).GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.NotEmpty(snapshot!.Queue);
        Assert.Contains(snapshot.Queue, item => item.Id == "08531720-7308-4867-9ec9-439b73b17d1f" && item.Status == DownloadQueueStatuses.Queued);
    }
}
