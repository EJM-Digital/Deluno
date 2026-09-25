using System.Globalization;
using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #661 — Transmission never requested <c>uploadRatio</c>/<c>secondsSeeding</c>,
/// so <see cref="DownloadQueueItem.Ratio"/>/<see cref="DownloadQueueItem.SeedingMinutes"/>
/// stayed null forever and a sharing rule could never be judged met. Its own
/// <c>isStalled</c> flag was never read either, so Deluno waited on a cruder,
/// generic no-throughput check instead of Transmission's own, better-informed
/// one. The peer count used every connected peer rather than
/// <c>peersSendingToUs</c>, and a person-stopped torrent (status 0) read
/// identically to one merely queued behind others.
/// </summary>
public sealed class TransmissionSharingAndStallTests
{
    [Fact]
    public async Task Ratio_and_seeding_minutes_are_read_from_the_torrent_get_response()
    {
        var item = await Snapshot(status: 6, percentDone: 1, uploadRatio: 2.5, secondsSeeding: 7200, peersSendingToUs: 0, isStalled: false);

        Assert.Equal(2.5, item.Ratio);
        Assert.Equal(120, item.SeedingMinutes);
    }

    [Fact]
    public async Task Seeding_minutes_is_null_when_transmission_never_reports_it()
    {
        var item = await Snapshot(status: 4, percentDone: 0.4, uploadRatio: null, secondsSeeding: null, peersSendingToUs: 2, isStalled: false);

        Assert.Null(item.Ratio);
        Assert.Null(item.SeedingMinutes);
    }

    [Fact]
    public async Task Transmissions_own_isStalled_flag_marks_the_item_stalled_immediately()
    {
        var item = await Snapshot(status: 4, percentDone: 0.4, uploadRatio: null, secondsSeeding: null, peersSendingToUs: 1, isStalled: true);

        Assert.Equal(DownloadQueueStatuses.Stalled, item.Status);
    }

    [Fact]
    public async Task IsStalled_false_leaves_an_actively_downloading_torrent_alone()
    {
        var item = await Snapshot(status: 4, percentDone: 0.4, uploadRatio: null, secondsSeeding: null, peersSendingToUs: 1, isStalled: false);

        Assert.Equal(DownloadQueueStatuses.Downloading, item.Status);
    }

    [Fact]
    public async Task IsStalled_does_not_override_an_error_or_a_completed_download()
    {
        var completed = await Snapshot(status: 6, percentDone: 1, uploadRatio: 1, secondsSeeding: 60, peersSendingToUs: 0, isStalled: true);

        Assert.Equal(DownloadQueueStatuses.ImportReady, completed.Status);
    }

    /// <summary>
    /// Peers still reflects connected peers, not merely ones sending payload
    /// data: a magnet exchanging metadata over a connected peer has sent no
    /// piece data yet, and switching Peers to peersSendingToUs would make
    /// ReconcileTorrentStatus's no-metadata-no-peers window read that as no
    /// connectivity at all and stall it on the first poll.
    /// </summary>
    [Fact]
    public async Task Peers_reflects_connected_peers_so_a_metadata_only_magnet_is_not_read_as_peerless()
    {
        var item = await Snapshot(status: 4, percentDone: 0.4, uploadRatio: null, secondsSeeding: null, peersSendingToUs: 0, isStalled: false, peersConnected: 5);

        Assert.Equal(5, item.Peers);
    }

    [Fact]
    public async Task Status_zero_is_paused_by_a_person_not_queued()
    {
        var item = await Snapshot(status: 0, percentDone: 0.2, uploadRatio: null, secondsSeeding: null, peersSendingToUs: 0, isStalled: false);

        Assert.Equal(DownloadQueueStatuses.Paused, item.Status);
    }

    [Fact]
    public async Task Status_one_still_reads_as_queued()
    {
        var item = await Snapshot(status: 1, percentDone: 0, uploadRatio: null, secondsSeeding: null, peersSendingToUs: 0, isStalled: false);

        Assert.Equal(DownloadQueueStatuses.Queued, item.Status);
    }

    private static async Task<DownloadQueueItem> Snapshot(
        int status,
        double percentDone,
        double? uploadRatio,
        long? secondsSeeding,
        int peersSendingToUs,
        bool isStalled,
        int peersConnected = 0)
    {
        var torrentGet = "{\"result\":\"success\",\"arguments\":{\"torrents\":[{\"id\":7,\"name\":\"Sherlock.Jr.1924.1080p.BluRay\","
            + $"\"status\":{status},\"percentDone\":{percentDone.ToString(CultureInfo.InvariantCulture)},"
            + $"\"totalSize\":5000000000,\"downloadedEver\":2000000000,\"peersConnected\":{peersConnected},\"peersSendingToUs\":{peersSendingToUs},"
            + $"\"addedDate\":{DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds()},\"rateDownload\":0,\"rateUpload\":0,\"eta\":0,"
            + $"\"isStalled\":{(isStalled ? "true" : "false")}"
            + (uploadRatio is null ? string.Empty : $",\"uploadRatio\":{uploadRatio.Value.ToString(CultureInfo.InvariantCulture)}")
            + (secondsSeeding is null ? string.Empty : $",\"secondsSeeding\":{secondsSeeding.Value.ToString(CultureInfo.InvariantCulture)}")
            + "}]}}";
        var handler = new TransmissionHandler(torrentGet);
        var client = new TransmissionDownloadClient(new Factory(handler));
        var snapshot = await client.GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    private static DownloadClientItem Client()
        => new("transmission-1", "Transmission", "transmission", "localhost", 9091, null, null, "http://localhost:9091/", "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class TransmissionHandler(string torrentGet) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var reply = body.Contains("\"torrent-get\"", StringComparison.Ordinal)
                ? torrentGet
                : "{\"result\":\"success\",\"arguments\":{}}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }
}
