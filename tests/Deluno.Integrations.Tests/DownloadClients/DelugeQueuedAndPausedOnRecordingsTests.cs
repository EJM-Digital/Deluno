using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1329: how a Deluge torrent that is waiting behind the active limit, and one a person paused, read in the queue. The list is Deluge
/// 2.2.0's own recorded reply to web.update_ui in each state (tests/fixtures/recorded/deluge).
/// </summary>
public sealed class DelugeQueuedAndPausedOnRecordingsTests
{
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static async Task<DownloadQueueItem> ReadAsync(string state)
    {
        var list = RecordedReplies.Deluge(state).Body("web.update_ui");
        var handler = RecordedDelugeRig.Handler(list);
        var client = new DownloadClientItem("deluge-1", "Deluge", "deluge", "localhost", 8112, null, null, "http://localhost:8112/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var snapshot = await new DelugeDownloadClient(new Factory(handler)).GetSnapshotAsync(client, DateTimeOffset.UtcNow, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    [Fact]
    public async Task A_torrent_Deluge_holds_in_its_queue_reads_Queued()
        => Assert.Equal(DownloadQueueStatuses.Queued, (await ReadAsync("queued")).Status);

    [Fact]
    public async Task A_torrent_a_person_paused_in_Deluge_reads_Paused()
        => Assert.Equal(DownloadQueueStatuses.Paused, (await ReadAsync("paused")).Status);
}
