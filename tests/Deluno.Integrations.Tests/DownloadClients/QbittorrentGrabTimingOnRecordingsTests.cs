using System.Text.Json.Nodes;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1301: a torrent qBittorrent 5.2.1 takes but does not list at once. The add is the client's own recorded answer (the JSON
/// object naming the torrent it took), the lists are its recorded empty list and its recorded list holding that torrent, and
/// the wait for the second is cut to nothing so the test does not sit through the real thirty seconds.
/// </summary>
public sealed class QbittorrentGrabTimingOnRecordingsTests
{
    private static string RecordedHash()
        => JsonNode.Parse(RecordedReplies.Qbittorrent("finished-single-file").Find("torrents/add").Reply.Body!)!["added_torrent_ids"]![0]!.GetValue<string>();

    private static DownloadClientGrabRequest Request(string hash)
        => new("Example.Movie.2026.1080p", "magnet:?xt=urn:btih:" + hash + "&dn=Example", "movies", "movies", "Example Indexer");

    [Fact]
    public async Task A_torrent_that_appears_in_the_list_a_moment_after_the_add_is_a_successful_grab_known_by_its_hash()
    {
        var hash = RecordedHash();
        var infoCalls = 0;
        var calls = new Calls("qbittorrent", [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version"), RecordedReplies.Qbittorrent("finished-single-file"), RecordedReplies.Qbittorrent("category-moved"), RecordedReplies.Qbittorrent("auto-management-set")],
            (request, _) =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("torrents/info", StringComparison.Ordinal)) return null;
                infoCalls++;
                // Empty before the add and for the first look after it, as a client behaves while it fetches the link.
                return RecordedReplies.Json(infoCalls <= 2
                    ? RecordedReplies.Qbittorrent("unknown-id").Body("torrents/info")
                    : RecordedReplies.Qbittorrent("finished-single-file").Body("torrents/info"));
            });
        var handler = calls.Build();

        var result = await new QbittorrentDownloadClient(() => handler).GrabAsync(Client("qbittorrent", "deluno"), Request(hash), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(hash, result.ExternalId);
        Assert.Equal(1, calls.Count("POST /api/v2/torrents/add"));
    }

    [Fact]
    public async Task A_torrent_the_client_took_but_never_lists_is_still_a_send_known_by_the_hash_when_the_add_said_it_took_it()
    {
        var hash = RecordedHash();
        var before = QbittorrentDownloadClient.ExtendedConfirmationDelay;
        QbittorrentDownloadClient.ExtendedConfirmationDelay = TimeSpan.Zero;
        try
        {
            var calls = new Calls("qbittorrent", [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version"), RecordedReplies.Qbittorrent("finished-single-file"), RecordedReplies.Qbittorrent("category-moved"), RecordedReplies.Qbittorrent("auto-management-set")],
                (request, _) => request.RequestUri!.AbsolutePath.EndsWith("torrents/info", StringComparison.Ordinal)
                    ? RecordedReplies.Json(RecordedReplies.Qbittorrent("unknown-id").Body("torrents/info"))
                    : null);
            var handler = calls.Build();

            var result = await new QbittorrentDownloadClient(() => handler)
                .GrabAsync(Client("qbittorrent", "deluno"), Request(hash) with { KnownInfoHash = hash }, CancellationToken.None);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(hash, result.ExternalId);
        }
        finally
        {
            QbittorrentDownloadClient.ExtendedConfirmationDelay = before;
        }
    }
}
