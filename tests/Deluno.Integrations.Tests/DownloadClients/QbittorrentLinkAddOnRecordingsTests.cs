using System.Text.Json.Nodes;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// A torrent sent to qBittorrent 5.2.4 as a link, not as a file (tests/fixtures/recorded/qbittorrent/5.2.4/add-link and add-link-dead).
/// The client answers an add by link at once with 202 and a count of jobs still pending (the object names no torrent), then fetches the
/// file itself and lists the torrent a moment later; a link the server answers 404 for is accepted the same way and the torrent never
/// appears. Both are the client's own recorded answers.
/// </summary>
public sealed class QbittorrentLinkAddOnRecordingsTests
{
    private static DownloadClientGrabRequest Request(string link)
        => new("Recorded.qBittorrent.Link.2008.1080p.WEB-DL.x264-E2E", link, "movies", "movies", "E2E feed");

    private static string LinkOf(Recording recording)
        => recording.Exchanges[0].Request.Body!.Split('&').Select(p => p.Split('=', 2)).First(p => p[0] == "urls")[1];

    [Fact]
    public async Task A_link_the_client_accepts_as_pending_is_a_grab_known_by_the_hash_it_lists_a_moment_later()
    {
        var link = RecordedReplies.Qbittorrent("add-link");
        var listed = JsonNode.Parse(link.Exchanges[2].Reply.Body!)!.AsArray()[0]!["hash"]!.GetValue<string>();
        var infoCalls = 0;
        var calls = new Calls("qbittorrent",
            [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version"), link, RecordedReplies.Qbittorrent("category-moved"), RecordedReplies.Qbittorrent("auto-management-set")],
            (request, _) =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("torrents/info", StringComparison.Ordinal)) return null;
                infoCalls++;
                // Nothing before the add and for the first look after it; then the client's own list holding the torrent.
                return infoCalls <= 2
                    ? RecordedReplies.Json(RecordedReplies.Qbittorrent("unknown-id").Body("torrents/info"))
                    : link.Exchanges[2].Reply.ToResponse();
            });
        var handler = calls.Build();

        var result = await new QbittorrentDownloadClient(() => handler).GrabAsync(Client("qbittorrent", "deluno"), Request(Uri.UnescapeDataString(LinkOf(link))), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(listed, result.ExternalId);
        Assert.Equal(1, calls.Count("POST /api/v2/torrents/add"));
        Assert.True(calls.Sent("urls="), "The link goes to qBittorrent as urls, not as a file.");
    }

    [Fact]
    public async Task A_link_that_is_dead_is_accepted_as_pending_and_never_listed_so_the_grab_is_not_a_success()
    {
        var dead = RecordedReplies.Qbittorrent("add-link-dead");
        var before = QbittorrentDownloadClient.ExtendedConfirmationDelay;
        QbittorrentDownloadClient.ExtendedConfirmationDelay = TimeSpan.Zero;
        try
        {
            var calls = new Calls("qbittorrent",
                [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version"), dead, RecordedReplies.Qbittorrent("category-moved"), RecordedReplies.Qbittorrent("auto-management-set")],
                (request, _) => request.RequestUri!.AbsolutePath.EndsWith("torrents/info", StringComparison.Ordinal)
                    ? RecordedReplies.Json(RecordedReplies.Qbittorrent("unknown-id").Body("torrents/info"))
                    : null);
            var handler = calls.Build();

            var result = await new QbittorrentDownloadClient(() => handler).GrabAsync(Client("qbittorrent", "deluno"), Request(Uri.UnescapeDataString(LinkOf(dead))), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Null(result.ExternalId);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
        }
        finally
        {
            QbittorrentDownloadClient.ExtendedConfirmationDelay = before;
        }
    }
}
