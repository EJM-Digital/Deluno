using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1306: a path a download client reports (its save folder, its content path, a finished job's folder) is never used
/// when it climbs out with ".." however the client came to say it. One helper, <c>SafePath.ReportedPath</c>, decides,
/// and every client's reading of a path goes through it: nothing downstream (the queue, an import, the leftover
/// cleanup, a delete with data) is handed a path that wanders out of the folder it names.
///
/// <para>Each test below takes the client's own recorded reply (qBittorrent 5.2.1, Transmission 4.1.3, Deluge 2.2.0,
/// SABnzbd 5.1.3, NZBGet 26.3) and swaps one value for a path with ".." in it: that is the only change, and a control
/// shows the unswapped recording still reads. uTorrent and rTorrent have no recording yet (see
/// tests/recorded-reply-allow-list.json, kind "pending"), so their tests use the same hand-built replies their other
/// tests do, with the same single value swapped; they say so where they are.</para>
/// </summary>
public sealed class APathAClientReportsThatClimbsOutIsNeverUsedTests
{
    private const string Climbing = @"C:\golden\recorded\somewhere\..\..\..\Windows\System32";
    private const string ClimbingPosix = "/downloads/movies/../../etc";

    private static DownloadClientItem Client(string protocol)
        => new("client-1", protocol, protocol, "localhost", 1234, "user", "secret", "http://localhost:1234/", false, null,
            "Movies", "TV", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Answering(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => answer(request, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
    }

    // ------------------------------------------------------------------ the shared rule

    [Theory]
    [InlineData(@"C:\Downloads\Completed\Movie", @"C:\Downloads\Completed\Movie")]
    [InlineData("/downloads/Movie..2020", "/downloads/Movie..2020")]
    [InlineData("  /downloads/Movie  ", "/downloads/Movie")]
    [InlineData("/downloads/../etc", null)]
    [InlineData(@"C:\Downloads\..\Windows", null)]
    [InlineData("/downloads/./Movie", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_reported_path_is_kept_only_when_it_does_not_climb(string? reported, string? expected)
        => Assert.Equal(expected, Deluno.Contracts.SafePath.ReportedPath(reported));

    // ------------------------------------------------------------------ qBittorrent (content_path, save_path)

    private static async Task<DownloadClientTelemetrySnapshot> QbittorrentSnapshot(params (string Field, object? Value)[] edits)
    {
        var handler = RecordedQbittorrentRig.Handler(RecordedQbittorrentRig.Info(RecordedQbittorrentRig.Finished, edits));
        return (await new QbittorrentDownloadClient(() => handler).GetSnapshotAsync(Client("qbittorrent"), DateTimeOffset.UtcNow, CancellationToken.None))!;
    }

    [Fact]
    public async Task Qbittorrent_the_recorded_content_path_is_read_as_it_is()
        => Assert.NotNull(Assert.Single((await QbittorrentSnapshot()).Queue).SourcePath);

    [Fact]
    public async Task Qbittorrent_a_content_path_and_save_path_with_dot_dot_swapped_into_the_recording_are_not_used()
    {
        var item = Assert.Single((await QbittorrentSnapshot(("content_path", Climbing), ("save_path", Climbing))).Queue);

        Assert.Null(item.SourcePath);
    }

    [Fact]
    public async Task Qbittorrent_a_climbing_content_path_falls_back_to_the_clean_save_path_and_never_keeps_the_climbing_one()
    {
        var clean = Assert.Single((await QbittorrentSnapshot()).Queue).SourcePath;
        var save = Assert.Single(RecordedReplies.Qbittorrent(RecordedQbittorrentRig.Finished).Find("torrents/info").Reply.Json().AsArray())!["save_path"]!.GetValue<string>();

        var item = Assert.Single((await QbittorrentSnapshot(("content_path", Climbing))).Queue);

        Assert.NotNull(clean);
        Assert.Equal(save, item.SourcePath);
        Assert.DoesNotContain("..", item.SourcePath);
    }

    // ------------------------------------------------------------------ Transmission (downloadDir + name)

    private static async Task<DownloadClientTelemetrySnapshot> TransmissionSnapshot(params (string Field, object? Value)[] edits)
    {
        var handler = RecordedTransmissionRig.Handler(RecordedTransmissionRig.Reply(RecordedTransmissionRig.Finished, edits));
        return (await new TransmissionDownloadClient(new Factory(handler)).GetSnapshotAsync(Client("transmission"), DateTimeOffset.UtcNow, CancellationToken.None))!;
    }

    [Fact]
    public async Task Transmission_the_recorded_download_folder_is_read_as_it_is()
        => Assert.NotNull(Assert.Single((await TransmissionSnapshot()).Queue).SourcePath);

    [Fact]
    public async Task Transmission_a_download_folder_with_dot_dot_swapped_into_the_recording_is_not_used()
        => Assert.Null(Assert.Single((await TransmissionSnapshot(("downloadDir", Climbing))).Queue).SourcePath);

    [Fact]
    public async Task Transmission_a_torrent_named_dot_dot_does_not_make_its_folder_climb()
        => Assert.Null(Assert.Single((await TransmissionSnapshot(("name", ".."))).Queue).SourcePath);

    // ------------------------------------------------------------------ Deluge (save_path + name)

    private static async Task<DownloadClientTelemetrySnapshot> DelugeSnapshot(params (string Field, object? Value)[] edits)
    {
        var handler = RecordedDelugeRig.Handler(RecordedDelugeRig.Finished(edits));
        return (await new DelugeDownloadClient(new Factory(handler)).GetSnapshotAsync(Client("deluge"), DateTimeOffset.UtcNow, CancellationToken.None))!;
    }

    [Fact]
    public async Task Deluge_the_recorded_save_path_is_read_as_it_is()
        => Assert.NotNull(Assert.Single((await DelugeSnapshot()).Queue).SourcePath);

    [Fact]
    public async Task Deluge_a_save_path_with_dot_dot_swapped_into_the_recording_is_not_used()
        => Assert.Null(Assert.Single((await DelugeSnapshot(("save_path", Climbing))).Queue).SourcePath);

    // ------------------------------------------------------------------ SABnzbd (history storage)

    private static async Task<IReadOnlyList<DownloadClientHistoryItem>> SabnzbdHistory(params (string Field, object? Value)[] edits)
    {
        var history = RecordedReplies.Sabnzbd("finished-folder").Body("mode=history&output", edits.Select(e => ("history.slots.0." + e.Field, e.Value)).ToArray());
        var handler = new Answering((request, _) => request.RequestUri!.Query.Contains("mode=history", StringComparison.Ordinal)
            ? RecordedReplies.Json(history)
            : RecordedReplies.Json(RecordedReplies.Sabnzbd("removed-without-data").Body("mode=queue&output", ("queue.slots", new System.Text.Json.Nodes.JsonArray()))));
        return await new SabnzbdDownloadClient(new Factory(handler)).GetHistoryAsync(Client("sabnzbd"), DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [Fact]
    public async Task Sabnzbd_the_recorded_finished_folder_is_read_as_it_is()
        => Assert.Equal(@"C:\Downloads\Completed\Recorded.Folder", (await SabnzbdHistory()).First().SourcePath);

    [Fact]
    public async Task Sabnzbd_a_storage_with_dot_dot_swapped_into_the_recording_is_not_used()
        => Assert.Null((await SabnzbdHistory(("storage", Climbing))).First().SourcePath);

    // ------------------------------------------------------------------ NZBGet (DestDir, FinalDir)

    private static async Task<IReadOnlyList<DownloadClientHistoryItem>> NzbgetHistory(string destDir)
    {
        var history = RecordedReplies.Nzbget("finished-single-file").Body(
            "history\"",
            ("^result", 1), ("result.0.Status", "SUCCESS/UNPACK"), ("result.0.Category", "TV"),
            ("result.0.NZBName", "Pioneer.One.S01E01.1080p"), ("result.0.Name", "Pioneer.One.S01E01.1080p"), ("result.0.DestDir", destDir));
        var handler = new Answering((_, _) => RecordedReplies.Json(history));
        return await new NzbGetDownloadClient(new Factory(handler)).GetHistoryAsync(Client("nzbget"), DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [Fact]
    public async Task Nzbget_a_job_folder_is_read_as_it_is()
        => Assert.Equal(@"C:\Downloads\Completed\TV\Pioneer.One.S01E01.1080p", Assert.Single(await NzbgetHistory(@"C:\Downloads\Completed\TV\Pioneer.One.S01E01.1080p")).SourcePath);

    [Fact]
    public async Task Nzbget_a_dest_dir_with_dot_dot_swapped_into_the_recording_is_not_used()
        => Assert.Null(Assert.Single(await NzbgetHistory(Climbing)).SourcePath);

    // ------------------------------------------------------------------ uTorrent (no recording yet: a hand-built list)

    [Fact]
    public async Task Utorrent_a_save_path_with_dot_dot_is_not_used_hand_built_reply_as_no_recording_exists()
    {
        object?[] Row(string savePath)
        {
            var row = new object?[28];
            Array.Fill(row, 0);
            row[0] = "0123456789abcdef0123456789abcdef01234567";
            row[1] = 136;
            row[2] = "Some.Film.2020";
            row[3] = 1_000_000;
            row[4] = 1000;
            row[11] = "movies";
            row[21] = string.Empty;
            row[26] = savePath;
            return row;
        }

        async Task<string?> Read(string savePath)
        {
            var list = System.Text.Json.JsonSerializer.Serialize(new { torrents = new[] { Row(savePath) } });
            var handler = new Answering((request, _) =>
                request.RequestUri!.AbsolutePath.EndsWith("token.html", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><div id='token'>t</div></html>") }
                    : request.RequestUri.Query.Contains("list=1", StringComparison.Ordinal)
                        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(list, Encoding.UTF8, "application/json") }
                        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
            var snapshot = await new UTorrentDownloadClient(() => handler).GetSnapshotAsync(Client("utorrent"), DateTimeOffset.UtcNow, CancellationToken.None);
            return Assert.Single(snapshot!.Queue).SourcePath;
        }

        Assert.Equal("/downloads/movies/Some.Film.2020", await Read("/downloads/movies/Some.Film.2020"));
        Assert.Null(await Read(ClimbingPosix));
    }

    // ------------------------------------------------------------------ deleting what a client's path names

    [Fact]
    public void A_delete_of_a_clients_files_refuses_a_path_that_climbs_even_when_it_resolves_to_something_real()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "deluno-climb-" + Guid.NewGuid().ToString("N"));
        try
        {
            var victim = Directory.CreateDirectory(Path.Combine(scratch, "victim")).FullName;
            File.WriteAllText(Path.Combine(victim, "keep.mkv"), "mine");
            Directory.CreateDirectory(Path.Combine(scratch, "completed"));
            var climbing = Path.Combine(scratch, "completed", "..", "victim");

            Assert.False(DownloadFolderRemoval.IsSafeToDelete(climbing, out _));
            Assert.True(DownloadFolderRemoval.IsSafeToDelete(victim, out _), "control: the same folder by its plain path is deletable");

            // The removal with a job name that would match, on a client that runs here: nothing is deleted.
            Assert.Null(DownloadFolderRemoval.Remove(climbing, "victim", onThisMachine: true));
            Assert.True(File.Exists(Path.Combine(victim, "keep.mkv")));
            Assert.Null(DownloadFolderRemoval.RemoveEmptyLeftovers(climbing, "victim"));
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }
}
