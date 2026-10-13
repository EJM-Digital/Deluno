using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1081: a poll of qBittorrent signs in once and keeps its session, asks about
/// a torrent's tracker only when the torrent is not moving data, and remembers
/// the answer for a minute, instead of a new connection, a login and one request
/// per torrent on every poll.
/// </summary>
public sealed class QbittorrentKeepsItsSessionTests
{
    [Fact]
    public async Task Polls_share_one_login_and_ask_about_trackers_only_for_idle_torrents_once()
    {
        var handler = new Handler();
        var client = new QbittorrentDownloadClient(() => handler);
        var item = Item();

        var first = await client.GetSnapshotAsync(item, DateTimeOffset.UtcNow, CancellationToken.None);
        var second = await client.GetSnapshotAsync(item, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(3, first!.Queue.Count);
        Assert.Equal(1, handler.Logins);
        // Two idle torrents asked about once; the one moving data never; nothing again on the second poll.
        Assert.Equal(new[] { "idle1", "idle2" }, handler.TrackerRequests.OrderBy(hash => hash).ToArray());
        Assert.NotNull(second);
        Assert.Equal(DownloadTrackerStatuses.Unregistered, second!.Queue.Single(q => q.Id == "idle1").TrackerStatus);
        Assert.Equal(DownloadTrackerStatuses.Ok, second.Queue.Single(q => q.Id == "idle2").TrackerStatus);
        Assert.Equal(DownloadTrackerStatuses.Unknown, second.Queue.Single(q => q.Id == "moving").TrackerStatus);
    }

    [Fact]
    public async Task A_refused_session_signs_in_again_and_the_poll_still_answers()
    {
        var handler = new Handler();
        var client = new QbittorrentDownloadClient(() => handler);
        var item = Item();
        await client.GetSnapshotAsync(item, DateTimeOffset.UtcNow, CancellationToken.None);

        handler.RefuseNextListing = true;
        var again = await client.GetSnapshotAsync(item, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.NotNull(again);
        Assert.Equal(3, again!.Queue.Count);
        Assert.Equal(2, handler.Logins);
    }

    private static DownloadClientItem Item()
        => new DownloadClientItem(
            "client-1", "qBittorrent", "qbittorrent", "localhost", 8080, null, null, "http://localhost:8080/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        with { Username = "user", Secret = "pw" };

    /// <summary>
    /// qBittorrent 5.2.1 as recorded: its sign-in, main data and tracker list, a refused session (the recorded 403), and a list of three
    /// torrents made from the recorded one real torrent (one moving data, two idle), each changed only in the values that tell them apart.
    /// </summary>
    private sealed class Handler : HttpMessageHandler
    {
        private int logins;

        public int Logins => Volatile.Read(ref logins);

        public bool RefuseNextListing { get; set; }

        public List<string> TrackerRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("auth/login", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref logins);
                return Task.FromResult(RecordedReplies.Qbittorrent("sign-in").Response("auth/login"));
            }

            // The recorded sign-in answers 204 and no text, so qBittorrent's session is checked with the version call, as it was recorded.
            if (path.EndsWith("app/version", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordedReplies.Qbittorrent("version").Response("app/version"));
            }

            if (path.EndsWith("torrents/info", StringComparison.Ordinal))
            {
                if (RefuseNextListing)
                {
                    RefuseNextListing = false;
                    return Task.FromResult(RecordedReplies.Qbittorrent("api-key-refused").Response("torrents/info"));
                }

                return Task.FromResult(RecordedReplies.Json(RecordedReplies.Qbittorrent("downloading").Body(
                    "torrents/info",
                    ("+", 3),
                    ("0.hash", "moving"), ("0.name", "Moving.Release"), ("0.state", "downloading"), ("0.dlspeed", 5000000),
                    ("1.hash", "idle1"), ("1.name", "Idle.One"), ("1.state", "stalledDL"), ("1.dlspeed", 0),
                    ("2.hash", "idle2"), ("2.name", "Idle.Two"), ("2.state", "stalledDL"), ("2.dlspeed", 0))));
            }

            if (path.EndsWith("torrents/trackers", StringComparison.Ordinal))
            {
                var hash = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["hash"]!;
                lock (TrackerRequests)
                {
                    TrackerRequests.Add(hash);
                }

                return Task.FromResult(RecordedReplies.Json(hash == "idle1"
                    ? RecordedQbittorrentRig.Trackers(4, "Torrent not registered with this tracker")
                    : RecordedQbittorrentRig.Trackers(2, string.Empty)));
            }

            if (path.EndsWith("sync/maindata", StringComparison.Ordinal))
            {
                return Task.FromResult(RecordedReplies.Qbittorrent("settings").Response("sync/maindata"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
