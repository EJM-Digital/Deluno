using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Platform.Contracts;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1198 and #1237, from the golden VM: the seeder stopped when the download was past 97% (a Usenet article never answered at 99%),
/// and each client's job stuck short of 100% with nothing arriving. Only 100% is finished, so a stuck job is still downloading on
/// every client, with its bytes the same at every poll, which is what lets Deluno's no-progress rule strike it. NZBGet's own is
/// checked in <see cref="AnNzbgetJobStuckNearTheEndIsNotCalledQueuedTests"/>.
/// </summary>
public sealed class ADownloadStuckShortOfTheEndIsStillDownloadingOnEveryClientTests
{
    private static DownloadClientItem Client(string protocol)
        => new($"{protocol}-1", protocol, protocol, "localhost", 8080, null, protocol == "sabnzbd" ? "api-key" : null, "http://localhost:8080/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static async Task<(DownloadQueueItem First, DownloadQueueItem Second)> TwoPolls(string app)
    {
        var client = Client(app);
        IDownloadClient implementation;
        switch (app)
        {
            case "qbittorrent":
                var qbit = RecordedQbittorrentRig.Handler(RecordedReplies.Qbittorrent("stalled-near-end").Body("torrents/info"));
                implementation = new QbittorrentDownloadClient(() => qbit);
                break;
            case "transmission":
                implementation = new TransmissionDownloadClient(new Factory(RecordedTransmissionRig.Handler(RecordedReplies.Transmission("stalled-near-end").Body("torrent-get"))));
                break;
            case "deluge":
                implementation = new DelugeDownloadClient(new Factory(RecordedDelugeRig.Handler(RecordedReplies.Deluge("stalled-near-end").Body("web.update_ui"))));
                break;
            default:
                // SABnzbd 5.1.3's recorded queue for a job stuck at 99% (beside two that are still fetching their NZB), and its recorded history.
                implementation = new SabnzbdDownloadClient(new Factory(RecordedReplies.Handler(
                    "sabnzbd",
                    [RecordedReplies.Sabnzbd("stalled-near-end"), RecordedReplies.Sabnzbd("idle"), RecordedReplies.Sabnzbd("version"), RecordedReplies.Sabnzbd("settings")])));
                break;
        }

        var first = (await implementation.GetSnapshotAsync(client, DateTimeOffset.UtcNow, CancellationToken.None))!;
        var second = (await implementation.GetSnapshotAsync(client, DateTimeOffset.UtcNow.AddMinutes(10), CancellationToken.None))!;
        return (Stuck(first), Stuck(second));
    }

    private static DownloadQueueItem Stuck(DownloadClientTelemetrySnapshot snapshot)
        => snapshot.Queue.SingleOrDefault(item => item.Progress is > 90 and < 100) ?? throw new InvalidOperationException(string.Join("; ", snapshot.Queue.Select(q => q.Title + " " + q.Status + " " + q.Progress)) + " / " + snapshot.HealthStatus + " " + snapshot.LastHealthMessage);

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("transmission")]
    [InlineData("deluge")]
    [InlineData("sabnzbd")]
    public async Task A_job_stuck_short_of_the_end_is_downloading_with_the_same_bytes_at_every_poll(string app)
    {
        var (first, second) = await TwoPolls(app);

        Assert.Equal(DownloadQueueStatuses.Downloading, first.Status);
        Assert.Equal(DownloadQueueStatuses.Downloading, second.Status);
        Assert.True(first.DownloadedBytes < first.SizeBytes, "short of its size");
        Assert.True(first.Progress < 100);
        Assert.Equal(first.DownloadedBytes, second.DownloadedBytes);
        Assert.False(DownloadHealthStrikes.IsFinishedPercent(first.Progress), "nothing short of 100% is finished");
    }
}
