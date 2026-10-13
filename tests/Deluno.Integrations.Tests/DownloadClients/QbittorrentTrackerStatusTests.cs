using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #878, cleanup sheet C5: qBittorrent's <c>torrents/info</c> carries no
/// tracker field at all — this adapter never called
/// <c>api/v2/torrents/trackers</c>, so it could not tell a tracker's own
/// "unregistered" rejection apart from an ordinary stall.
/// </summary>
public sealed class QbittorrentTrackerStatusTests
{
    [Fact]
    public async Task A_not_working_tracker_reporting_unregistered_normalizes_to_the_tracker_unregistered_status()
    {
        var item = await Snapshot(trackersJson: RecordedQbittorrentRig.Trackers(4, "torrent not registered with this tracker"));

        Assert.Equal(DownloadTrackerStatuses.Unregistered, item.TrackerStatus);
    }

    [Fact]
    public async Task A_working_tracker_normalizes_to_ok()
    {
        var item = await Snapshot(trackersJson: RecordedQbittorrentRig.Trackers(2, string.Empty));

        Assert.Equal(DownloadTrackerStatuses.Ok, item.TrackerStatus);
    }

    /// <summary>A tracker merely unreachable right now (status 4, but not the "gone for good" wording) is not read as unregistered.</summary>
    [Fact]
    public async Task A_not_working_tracker_with_an_unrelated_message_is_not_read_as_unregistered()
    {
        var item = await Snapshot(trackersJson: RecordedQbittorrentRig.Trackers(4, "Connection refused"));

        Assert.Equal(DownloadTrackerStatuses.Ok, item.TrackerStatus);
    }

    [Fact]
    public async Task No_trackers_at_all_normalizes_to_ok_not_unregistered()
    {
        var item = await Snapshot(trackersJson: RecordedReplies.Qbittorrent("trackers").Body("torrents/trackers", ("^", 0)));

        Assert.Equal(DownloadTrackerStatuses.Ok, item.TrackerStatus);
    }

    /// <summary>A tracker read that fails outright (#901) says nothing either way - it never reads as unregistered.</summary>
    [Fact]
    public async Task A_failed_tracker_read_normalizes_to_unknown_not_unregistered()
    {
        var item = await Snapshot(trackersJson: null);

        Assert.Equal(DownloadTrackerStatuses.Unknown, item.TrackerStatus);
    }

    private static async Task<DownloadQueueItem> Snapshot(string? trackersJson)
    {
        // qBittorrent 5.2.1's recorded list of a download in progress, with the tracker list as under test (null: the tracker read fails).
        var torrentsInfo = RecordedQbittorrentRig.Info(RecordedQbittorrentRig.Downloading, ("category", "movies"), ("state", "downloading"), ("progress", 0.4), ("dlspeed", 0), ("upspeed", 0));
        var handler = RecordedQbittorrentRig.Handler(
            torrentsInfo,
            trackersJson,
            (request, _) => trackersJson is null && request.RequestUri!.AbsolutePath.Contains("torrents/trackers", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : null);
        var client = new QbittorrentDownloadClient(() => handler);
        var snapshot = await client.GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    private static DownloadClientItem Client()
        => new("qbit-1", "qBittorrent", "qbittorrent", "localhost", 8080, null, null, "http://localhost:8080/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
