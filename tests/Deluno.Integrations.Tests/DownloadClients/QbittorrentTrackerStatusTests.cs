using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

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
        var item = await Snapshot(trackersJson: """[{"status":4,"msg":"torrent not registered with this tracker"}]""");

        Assert.Equal(DownloadTrackerStatuses.Unregistered, item.TrackerStatus);
    }

    [Fact]
    public async Task A_working_tracker_normalizes_to_ok()
    {
        var item = await Snapshot(trackersJson: """[{"status":2,"msg":""}]""");

        Assert.Equal(DownloadTrackerStatuses.Ok, item.TrackerStatus);
    }

    /// <summary>A tracker merely unreachable right now (status 4, but not the "gone for good" wording) is not read as unregistered.</summary>
    [Fact]
    public async Task A_not_working_tracker_with_an_unrelated_message_is_not_read_as_unregistered()
    {
        var item = await Snapshot(trackersJson: """[{"status":4,"msg":"Connection refused"}]""");

        Assert.Equal(DownloadTrackerStatuses.Ok, item.TrackerStatus);
    }

    [Fact]
    public async Task No_trackers_at_all_normalizes_to_ok_not_unregistered()
    {
        var item = await Snapshot(trackersJson: "[]");

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
        var torrentsInfo = "[{\"hash\":\"abc123\",\"name\":\"Sherlock.Jr.1924.1080p.BluRay\",\"category\":\"movies\","
            + "\"state\":\"downloading\",\"progress\":0.4,"
            + "\"dlspeed\":0,\"upspeed\":0,\"eta\":0,\"size\":5000000000,\"downloaded\":2000000000,"
            + "\"num_seeds\":0,\"num_leechs\":0,"
            + $"\"added_on\":{DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds()},\"save_path\":\"/downloads\",\"content_path\":\"/downloads/item\"}}]";
        var handler = new QbittorrentHandler(torrentsInfo, trackersJson);
        var client = new QbittorrentDownloadClient(() => handler);
        var snapshot = await client.GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);
        return Assert.Single(snapshot!.Queue);
    }

    private static DownloadClientItem Client()
        => new("qbit-1", "qBittorrent", "qbittorrent", "localhost", 8080, null, null, "http://localhost:8080/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class QbittorrentHandler(string torrentsInfo, string? trackersJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("auth/login", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.", Encoding.UTF8, "text/plain") });
            }

            if (path.Contains("torrents/trackers", StringComparison.Ordinal))
            {
                return trackersJson is null
                    ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
                    : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(trackersJson, Encoding.UTF8, "application/json") });
            }

            var reply = path.Contains("torrents/info", StringComparison.Ordinal) ? torrentsInfo : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") });
        }
    }
}
