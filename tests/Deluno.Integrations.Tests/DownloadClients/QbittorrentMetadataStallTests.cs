using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// qBittorrent's <c>metaDL</c> (fetching metadata, no peers yet) and
/// <c>stalledDL</c> (gave up finding peers) are the same case for Deluno
/// (#650): no metadata ever arriving. <c>stalledDL</c> already reads as
/// stalled immediately through its status text; <c>metaDL</c> must reach the
/// same place once it has gone unmetadata'd for the stall window #618 gave
/// Deluge, rather than sitting as "downloading" for ever.
/// </summary>
public sealed class QbittorrentMetadataStallTests
{
    [Fact]
    public async Task StalledDL_is_stalled_immediately()
    {
        var addedUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var item = await Snapshot("stalledDL", size: 0, progress: 0, addedUtc, capturedUtc: addedUtc.AddMinutes(1));

        Assert.Equal(DownloadQueueStatuses.Stalled, item.Status);
    }

    [Fact]
    public async Task MetaDL_is_downloading_before_the_stall_window()
    {
        var addedUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        var item = await Snapshot("metaDL", size: 0, progress: 0, addedUtc, capturedUtc: addedUtc.AddMinutes(5));

        Assert.Equal(DownloadQueueStatuses.Downloading, item.Status);
    }

    [Fact]
    public async Task MetaDL_becomes_stalled_once_it_has_gone_unmetadatad_past_the_stall_window()
    {
        var addedUtc = DateTimeOffset.UtcNow.AddMinutes(-40);
        var item = await Snapshot("metaDL", size: 0, progress: 0, addedUtc, capturedUtc: addedUtc.AddMinutes(40));

        Assert.Equal(DownloadQueueStatuses.Stalled, item.Status);
    }

    [Fact]
    public async Task Completion_is_never_inferred_from_zero_size_alone()
    {
        var addedUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var item = await Snapshot("downloading", size: 0, progress: 1, addedUtc, capturedUtc: addedUtc.AddMinutes(1));

        Assert.NotEqual(DownloadQueueStatuses.ImportReady, item.Status);
    }

    private static async Task<DownloadQueueItem> Snapshot(
        string state,
        long size,
        double progress,
        DateTimeOffset addedUtc,
        DateTimeOffset capturedUtc)
    {
        var torrentsInfo = "[{\"hash\":\"abc123\",\"name\":\"The.Last.of.Us.S01E07.2023.720p.WEB-DL\",\"category\":\"tv\","
            + $"\"state\":\"{state}\",\"progress\":{progress.ToString(System.Globalization.CultureInfo.InvariantCulture)},"
            + $"\"dlspeed\":0,\"upspeed\":0,\"eta\":8640000,\"size\":{size},\"downloaded\":0,\"num_seeds\":0,"
            + $"\"added_on\":{addedUtc.ToUnixTimeSeconds()},\"save_path\":\"/downloads\",\"content_path\":\"/downloads/item\"}}]";
        var handler = new QbittorrentHandler(torrentsInfo);
        var client = new QbittorrentDownloadClient(() => handler);
        var snapshot = await client.GetSnapshotAsync(Client(), capturedUtc, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    private static DownloadClientItem Client()
        => new("qbit-1", "qBittorrent", "qbittorrent", "localhost", 8080, null, null, "http://localhost:8080/", "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class QbittorrentHandler(string torrentsInfo) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var reply = path.Contains("auth/login", StringComparison.Ordinal)
                ? "Ok."
                : path.Contains("torrents/info", StringComparison.Ordinal)
                    ? torrentsInfo
                    : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") });
        }
    }
}
