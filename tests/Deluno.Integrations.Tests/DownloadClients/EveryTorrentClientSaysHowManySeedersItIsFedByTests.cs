using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Platform.Contracts;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1516: a download's reading says how many of its connected peers are seeders, and, where the client reports it, how many seeders the
/// whole swarm has. Each torrent client's adapter reads it from that client's recorded reply; Peers keeps meaning every connected peer.
/// </summary>
public sealed class EveryTorrentClientSaysHowManySeedersItIsFedByTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task QBittorrent_reads_seeders_from_num_seeds_and_the_swarm_from_num_complete()
    {
        // Recorded 5.2.1 download: num_seeds 2, num_leechs 0, num_complete 1. The swarm count is edited to the figure this is about.
        var info = RecordedQbittorrentRig.Info("downloading", ("num_complete", 41));
        var handler = RecordedQbittorrentRig.Handler(info);
        var snapshot = await new QbittorrentDownloadClient(() => handler).GetSnapshotAsync(Client("qbittorrent"), Now, CancellationToken.None);

        var item = Assert.Single(snapshot!.Queue);
        Assert.Equal(2, item.Seeders);
        Assert.Equal(41, item.SwarmSeeders);
        Assert.Equal(2, item.Peers);
    }

    [Fact]
    public async Task QBittorrent_peers_still_count_leechers_beside_seeders()
    {
        var handler = RecordedQbittorrentRig.Handler(RecordedQbittorrentRig.Info("downloading", ("num_seeds", 2), ("num_leechs", 3)));
        var item = Assert.Single((await new QbittorrentDownloadClient(() => handler).GetSnapshotAsync(Client("qbittorrent"), Now, CancellationToken.None))!.Queue);

        Assert.Equal(2, item.Seeders);
        Assert.Equal(5, item.Peers);
    }

    [Fact]
    public async Task Deluge_reads_seeders_from_num_seeds()
    {
        // Recorded 2.2.0 download: num_seeds 2, num_peers 0.
        var handler = RecordedDelugeRig.Handler(RecordedDelugeRig.Downloading());
        var item = Assert.Single((await new DelugeDownloadClient(new Factory(handler)).GetSnapshotAsync(Client("deluge"), Now, CancellationToken.None))!.Queue);

        Assert.Equal(2, item.Seeders);
        Assert.Equal(2, item.Peers);
        Assert.Null(item.SwarmSeeders);
    }

    [Fact]
    public async Task Transmission_reads_seeders_from_peersSendingToUs_and_the_swarm_from_the_trackers()
    {
        // Recorded 4.1.3 download: peersConnected 1, peersSendingToUs 1, trackerStats.0.seederCount 1.
        var handler = RecordedTransmissionRig.Handler(RecordedTransmissionRig.Reply("downloading"));
        var item = Assert.Single((await new TransmissionDownloadClient(new Factory(handler)).GetSnapshotAsync(Client("transmission"), Now, CancellationToken.None))!.Queue);

        Assert.Equal(1, item.Seeders);
        Assert.Equal(1, item.SwarmSeeders);
        Assert.Equal(1, item.Peers);
    }

    [Fact]
    public async Task Transmission_does_not_invent_a_swarm_when_its_trackers_do_not_know()
    {
        // A tracker that has not scraped reports seederCount -1.
        var handler = RecordedTransmissionRig.Handler(RecordedTransmissionRig.Reply("downloading", ("trackerStats.0.seederCount", -1)));
        var item = Assert.Single((await new TransmissionDownloadClient(new Factory(handler)).GetSnapshotAsync(Client("transmission"), Now, CancellationToken.None))!.Queue);

        Assert.Null(item.SwarmSeeders);
    }

    [Fact]
    public async Task UTorrent_reads_seeders_from_its_seeds_connected_column()
    {
        // The recorded stalled list has the seeds-connected cell (index 14) at 0; the test sets it to the figure it is about.
        var list = RecordedReplies.Utorrent("stalled").Exchanges.Last(e => e.Request.Path.Contains("list=1", StringComparison.Ordinal)).Reply.Body!;
        var node = System.Text.Json.Nodes.JsonNode.Parse(list)!;
        var row = node["torrents"]![0]!.AsArray();
        row[14] = 3;
        row[12] = 1;
        var handler = new UTorrentHandler(node.ToJsonString());
        var item = (await new UTorrentDownloadClient(() => handler).GetSnapshotAsync(Client("utorrent"), Now, CancellationToken.None))!.Queue.First();

        Assert.Equal(3, item.Seeders);
        Assert.Equal(4, item.Peers);
    }

    private static DownloadClientItem Client(string kind)
        => new(kind + "-1", kind, kind, "localhost", 8080, null, null, "http://localhost:8080/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class UTorrentHandler(string list) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var body = uri.AbsolutePath.EndsWith("token.html", StringComparison.Ordinal) ? RecordedReplies.UtorrentTokenPage()
                : uri.Query.Contains("list=1", StringComparison.Ordinal) ? list
                : uri.Query.Contains("action=getsettings", StringComparison.Ordinal) ? RecordedReplies.Utorrent("settings").Single.Reply.Body!
                : "{}";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
