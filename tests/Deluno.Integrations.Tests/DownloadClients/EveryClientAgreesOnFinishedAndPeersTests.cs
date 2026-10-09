using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Platform.Contracts;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1198: one finished rule for every adapter. Only 100% (or the client's own complete state) is finished; there is no
/// 99.9% threshold anywhere, so a download stuck at 99.93% is still downloading and is struck. And a client's peer
/// count includes its seeds, so a healthy download fed by one seed does not trip "Not enough peers".
/// </summary>
public sealed class EveryClientAgreesOnFinishedAndPeersTests
{
    [Theory]
    [InlineData(99.0, false)]
    [InlineData(99.9, false)]
    [InlineData(99.939, false)]
    [InlineData(99.999, false)]
    [InlineData(100.0, true)]
    public void The_shared_rule_calls_only_a_hundred_percent_finished(double percent, bool finished)
        => Assert.Equal(finished, DownloadHealthStrikes.IsFinishedPercent(percent));

    [Theory]
    [InlineData(99.939, DownloadQueueStatuses.Downloading)]
    [InlineData(99.99, DownloadQueueStatuses.Downloading)]
    [InlineData(100.0, DownloadQueueStatuses.ImportReady)]
    public void Deluge_reads_a_percent_short_of_complete_as_downloading(double percent, string expected)
        => Assert.Equal(expected, new DelugeDownloadClient(new NoFactory()).NormalizeStatus("Downloading", percent));

    [Theory]
    [InlineData(99.927, DownloadQueueStatuses.Downloading)]
    [InlineData(100.0, DownloadQueueStatuses.ImportReady)]
    public void Sabnzbd_reads_a_percent_short_of_complete_as_downloading(double percent, string expected)
        => Assert.Equal(expected, new SabnzbdDownloadClient(new NoFactory()).NormalizeStatus("Downloading", percent));

    [Theory]
    [InlineData(99.95, DownloadQueueStatuses.Downloading)]
    [InlineData(100.0, DownloadQueueStatuses.ImportReady)]
    public void The_shared_text_path_and_uTorrent_agree(double percent, string expected)
    {
        Assert.Equal(expected, DownloadClientHelpers.NormalizeTextStatus("Downloading", percent));
        Assert.Equal(expected, new UTorrentDownloadClient(() => new HttpClientHandler()).NormalizeStatus("1", percent));
    }

    [Theory]
    [InlineData(99.9, DownloadQueueStatuses.Downloading)]
    [InlineData(100.0, DownloadQueueStatuses.ImportReady)]
    public void RTorrent_agrees(double percent, string expected)
        => Assert.Equal(expected, new RtorrentDownloadClient(new NoFactory()).NormalizeStatus("downloading", percent));

    [Theory]
    [InlineData(0.9995, DownloadQueueStatuses.Downloading)]
    [InlineData(1.0, DownloadQueueStatuses.ImportReady)]
    public void QBittorrent_and_Transmission_agree(double fraction, string expected)
    {
        Assert.Equal(expected, new QbittorrentDownloadClient(() => new HttpClientHandler()).NormalizeStatus("downloading", fraction));
        Assert.Equal(expected, new TransmissionDownloadClient(new NoFactory()).NormalizeStatus("4", fraction));
    }

    [Fact]
    public async Task Deluge_peers_include_its_seeds()
    {
        var json = """{"result":{"torrents":{"abc123":{"name":"Sample.Film.2026.1080p","state":"Downloading","progress":40,"download_payload_rate":100000,"upload_payload_rate":0,"eta":60,"total_size":1000,"total_done":400,"num_peers":0,"num_seeds":1,"time_added":1700000000,"label":"movies","message":"OK","save_path":"/downloads"}}},"error":null,"id":2}""";
        var snapshot = await new DelugeDownloadClient(new Factory(new DelugeHandler(json)))
            .GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, Assert.Single(snapshot!.Queue).Peers);
    }

    private static DownloadClientItem Client()
        => new("deluge-1", "Deluge", "deluge", "localhost", 8112, null, null, "http://localhost:8112/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class NoFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelugeHandler(string updateUi) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var answer = body.Contains("web.update_ui", StringComparison.Ordinal) ? updateUi : """{"result":true,"error":null,"id":1}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }
}
