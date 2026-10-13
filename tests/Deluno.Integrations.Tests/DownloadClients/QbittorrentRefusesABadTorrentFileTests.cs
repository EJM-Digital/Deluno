using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1237, from qBittorrent 5.2.1's recorded reply to an upload that is not a torrent: HTTP 415 and the text
/// "Error: 'file.torrent' is not a valid torrent file." The grab only treated a "Fails." body or a 409 as the client
/// turning the release down, so a 415 threw as if the client were down: the release was never marked dead, was not
/// blocklisted, and the next search could pick the same broken file again.
/// </summary>
public sealed class QbittorrentRefusesABadTorrentFileTests
{
    private static readonly byte[] NotATorrent = System.Text.Encoding.Latin1.GetBytes("this is not a torrent");

    private static DownloadClientGrabRequest Request(byte[]? file)
        => new("Example.Movie.2026.1080p", "https://indexer.example/dl/1.torrent", "movies", "movies", "Example Indexer") { TorrentFile = file };

    private static DownloadClientItem Client()
        => new(
            "client-1", "qBittorrent", "qbittorrent", "localhost", 8080, null, "secret", "http://localhost:8080/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    /// <summary>qBittorrent 5.2.1 as recorded: its sign-in, an empty list, and the 415 it answered to a file that is not a torrent.</summary>
    private static HttpMessageHandler Qbittorrent()
        => RecordedReplies.Handler(
            "qbittorrent",
            [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version"), RecordedReplies.Qbittorrent("add-refused"), RecordedReplies.Qbittorrent("idle")]);

    [Fact]
    public async Task A_file_qbittorrent_answers_415_to_is_a_dead_release_not_a_client_that_is_down()
    {
        var result = await new QbittorrentDownloadClient(() => Qbittorrent()).GrabAsync(Client(), Request(NotATorrent), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(DownloadQueueStatuses.DeadRelease, result.Status);
        Assert.Equal(415, result.ResponseCode);
        Assert.Contains("not a valid torrent file", result.ResponseJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_415_for_a_file_with_a_known_hash_is_also_a_dead_release()
    {
        // A file whose hash Deluno worked out (a real .torrent that qBittorrent still turned down) is judged the same way.
        var request = Request(NotATorrent) with { KnownInfoHash = "de4158016fbc49ebf35641903cec65a0ad0d30b5" };

        var result = await new QbittorrentDownloadClient(() => Qbittorrent()).GrabAsync(Client(), request, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(DownloadQueueStatuses.DeadRelease, result.Status);
    }
}
