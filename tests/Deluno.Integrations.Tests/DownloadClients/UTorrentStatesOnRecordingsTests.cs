using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1421: the status of a uTorrent torrent read from the list rows the real uTorrent 3.6 sent (recorded with the client limited to
/// one active download, two torrents added, then the first one paused). A paused torrent carries the Queued bit too (233 = loaded,
/// queued, paused, checked, started), so the paused bit has to be read before the queued one.
/// </summary>
public sealed class UTorrentStatesOnRecordingsTests
{
    // The two torrents of the recording: "Recorded.Stalled.One" (first added) and "Recorded.Stalled.Two".
    private const string One = "AD75B0674EDE482D905547AF476320D8BE05FA7A";
    private const string Two = "86693FF52E864909897DF8D6743E84024B86F995";

    [Fact]
    public async Task A_torrent_waiting_behind_the_active_limit_reads_Queued()
    {
        var queue = await Read("queued");

        Assert.Equal(DownloadQueueStatuses.Queued, Assert.Single(queue, i => i.Id == Two).Status);
    }

    [Fact]
    public async Task A_torrent_that_is_downloading_reads_Downloading()
    {
        var queue = await Read("queued");

        Assert.Equal(DownloadQueueStatuses.Downloading, Assert.Single(queue, i => i.Id == One).Status);
    }

    [Fact]
    public async Task A_paused_torrent_reads_Paused()
    {
        var queue = await Read("paused");

        Assert.Equal(DownloadQueueStatuses.Paused, Assert.Single(queue, i => i.Id == One).Status);
    }

    [Fact]
    public async Task Pausing_one_torrent_leaves_the_other_as_it_was()
    {
        var queue = await Read("paused");

        // With the limit lifted, the second torrent left the queue and started.
        Assert.Equal(DownloadQueueStatuses.Downloading, Assert.Single(queue, i => i.Id == Two).Status);
    }

    private static async Task<IReadOnlyList<DownloadQueueItem>> Read(string state)
    {
        var token = RecordedReplies.Utorrent("token").Single.Reply.Body!.Replace("<scrubbed>", "recorded-token");
        var list = RecordedReplies.Utorrent(state).Exchanges.Last(e => e.Request.Path.Contains("list=1", StringComparison.Ordinal)).Reply.Body!;
        var handler = new Answer(request =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("token.html", StringComparison.Ordinal)) return new StringContent(token, Encoding.UTF8);
            if (uri.Contains("list=1", StringComparison.Ordinal)) return new StringContent(list, Encoding.UTF8);
            // getprops (the seed time of each torrent) is not part of these states; the status does not read it.
            return new StringContent("{}", Encoding.UTF8);
        });
        var snapshot = await new UTorrentDownloadClient(() => handler).GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);
        return snapshot!.Queue;
    }

    private static DownloadClientItem Client()
        => new("utorrent-1", "uTorrent", "utorrent", "localhost", 8081, null, null, "http://localhost:8081/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Answer(Func<HttpRequestMessage, HttpContent> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = reply(request) });
    }
}
