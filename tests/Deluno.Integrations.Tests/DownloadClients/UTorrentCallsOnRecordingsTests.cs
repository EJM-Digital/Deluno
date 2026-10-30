using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>uTorrent's replies to calls on a torrent it does not have, and to stop/start, as recorded from uTorrent 3.6 (#1421).</summary>
public sealed class UTorrentCallsOnRecordingsTests
{
    [Fact]
    public async Task The_files_of_a_torrent_uTorrent_does_not_have_are_none()
    {
        var handler = RecordedReplies.Handler("utorrent", [RecordedReplies.Utorrent("token"), RecordedReplies.Utorrent("files-unknown-hash")]);

        var files = await new UTorrentDownloadClient(() => handler).GetFilesAsync(Client(), new string('0', 40), CancellationToken.None);

        Assert.Null(files);
    }

    [Fact]
    public async Task A_label_for_a_torrent_uTorrent_does_not_have_is_not_reported_as_set()
    {
        var handler = RecordedReplies.Handler("utorrent", [RecordedReplies.Utorrent("token"), RecordedReplies.Utorrent("idle"), RecordedReplies.Utorrent("label-unknown-hash")]);

        var result = await new UTorrentDownloadClient(() => handler).SetCategoryAsync(Client(), new string('0', 40), "movies", null, CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Resuming_a_stopped_torrent_sends_start_for_its_hash()
    {
        var recording = RecordedReplies.Utorrent("start-stop-force");
        using var rows = System.Text.Json.JsonDocument.Parse(recording.Exchanges.First(e => e.Request.Path.Contains("list=1", StringComparison.Ordinal)).Reply.Body!);
        var hash = rows.RootElement.GetProperty("torrents")[0][0].GetString()!;
        var sent = new List<string>();
        var handler = RecordedReplies.Handler("utorrent", [RecordedReplies.Utorrent("token"), recording],
            (request, _) => { sent.Add(request.RequestUri!.Query); return null; });

        await new UTorrentDownloadClient(() => handler).ExecuteActionAsync(Client(), DownloadClientActions.Resume, hash, CancellationToken.None);

        // The torrent is stopped (136) in the first list the recording answers with, so the resume goes out as "start".
        Assert.Contains(sent, query => query.Contains("action=start&", StringComparison.Ordinal) && query.Contains("hash=" + hash, StringComparison.Ordinal));
    }

    private static DownloadClientItem Client()
        => new("utorrent-1", "uTorrent", "utorrent", "localhost", 8081, null, null, "http://localhost:8081/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
