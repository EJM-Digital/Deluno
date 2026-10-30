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

    // #1421: after "start" uTorrent lists the torrent as 200 (queued to run, not Paused): that is resumed, not "not running".
    [Fact]
    public async Task A_stopped_torrent_that_uTorrent_then_queues_to_run_counts_as_resumed()
    {
        var recording = RecordedReplies.Utorrent("start-stop-force");
        using var rows = System.Text.Json.JsonDocument.Parse(recording.Exchanges.First(e => e.Request.Path.Contains("list=1", StringComparison.Ordinal)).Reply.Body!);
        var hash = rows.RootElement.GetProperty("torrents")[0][0].GetString()!;
        var lists = recording.Exchanges.Where(e => e.Request.Path.Contains("list=1", StringComparison.Ordinal)).Select(e => e.Reply.Body!).ToArray();
        var started = false;
        var handler = RecordedReplies.Handler("utorrent", [RecordedReplies.Utorrent("token")], (request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("action=start", StringComparison.Ordinal)) { started = true; return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(recording.Exchanges.First(e => e.Request.Path.Contains("action=start&", StringComparison.Ordinal)).Reply.Body!) }; }
            // stopped (136) until the start is sent, then queued to run (200), as the recording shows
            return uri.Contains("list=1", StringComparison.Ordinal) ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(lists[started ? 1 : 0]) } : null;
        });

        var result = await new UTorrentDownloadClient(() => handler).ExecuteActionAsync(Client(), DownloadClientActions.Resume, hash, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
    }

    [Theory]
    [InlineData(136, true)]   // stopped
    [InlineData(233, true)]   // paused
    [InlineData(200, false)]  // queued to run
    [InlineData(201, false)]  // downloading
    public void Only_a_stopped_or_paused_torrent_is_not_running(int bits, bool notRunning)
        => Assert.Equal(notRunning, UTorrentDownloadClient.IsPaused(bits));

    private static DownloadClientItem Client()
        => new("utorrent-1", "uTorrent", "utorrent", "localhost", 8081, null, null, "http://localhost:8081/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
