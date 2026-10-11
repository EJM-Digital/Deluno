using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1252, from the scenario suite on rc.30.21 (remove-copy.*): a second "Remove this copy" of the same finished
/// download answered HTTP 200 "You removed the leftover copy ... and deleted its files" on qBittorrent and Transmission,
/// and wrote a second Activity row, because #1212's look (the client had the download before, and no longer has it
/// after) was only added to SABnzbd and NZBGet. And an NZBGet removal left the job's own folder in the completed
/// folder, because #1224 removed it for SABnzbd only.
///
/// <para>Every reply below is a recorded real reply (qBittorrent 5.2.1, Transmission 4.1.3, Deluge 2.2.0, NZBGet 26.3).
/// The stand-in client holds one download, answers the list call from the recording of a finished download while it
/// has it and from the recording of the removed one after the delete call, which is what the real client did.</para>
/// </summary>
public sealed class EveryClientRemovesOnlyWhatItHasTests
{
    private static readonly string[] RemovingActions =
        [DownloadClientActions.Delete, DownloadClientActions.DeleteWithData, DownloadClientActions.RemoveRefusedCopy];

    /// <summary>A recorded client that holds one download until the delete call arrives.</summary>
    private sealed class Rig(string app, Func<bool, HttpRequestMessage, string, HttpResponseMessage?> listing, Recording[] recordings, string deleteCall, bool has, bool keepsItAfterDelete, bool listingFails)
    {
        private bool _has = has;

        public int Deletes { get; private set; }

        public HttpMessageHandler Handler => RecordedReplies.Handler(app, recordings, (request, body) =>
        {
            if ((request.RequestUri!.PathAndQuery + body).Contains(deleteCall, StringComparison.Ordinal))
            {
                Deletes++;
                if (!keepsItAfterDelete) _has = false;
                return null;
            }

            return listingFails ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : listing(_has, request, body);
        });
    }

    private const string Hash = "3b1d84012faffbb5c3c8d00d92358d52f2c856eb";

    private static Rig RigFor(string app, bool has = true, bool keepsItAfterDelete = false, bool listingFails = false)
        => app switch
        {
            "qbittorrent" => new Rig("qbittorrent",
                (present, request, _) => request.RequestUri!.AbsolutePath.EndsWith("torrents/info", StringComparison.Ordinal)
                    ? (present ? RecordedReplies.Qbittorrent("finished-folder") : RecordedReplies.Qbittorrent("removed-twice")).Find("torrents/info").Reply.ToResponse()
                    : null,
                [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("removed-with-data")],
                "torrents/delete", has, keepsItAfterDelete, listingFails),
            "transmission" => new Rig("transmission",
                (present, _, body) => body.Contains("torrent-get", StringComparison.Ordinal)
                    ? (present ? RecordedReplies.Transmission("finished-folder") : RecordedReplies.Transmission("removed-twice")).Find("torrent-get").Reply.ToResponse()
                    : null,
                [RecordedReplies.Transmission("session-handshake"), RecordedReplies.Transmission("removed-with-data")],
                "torrent-remove", has, keepsItAfterDelete, listingFails),
            _ => new Rig("deluge",
                (present, _, body) => body.Contains("web.update_ui", StringComparison.Ordinal)
                    ? (present ? RecordedReplies.Deluge("finished-folder") : RecordedReplies.Deluge("removed-twice")).Find("web.update_ui").Reply.ToResponse()
                    : null,
                [RecordedReplies.Deluge("sign-in"), RecordedReplies.Deluge("removed-with-data")],
                "core.remove_torrent", has, keepsItAfterDelete, listingFails)
        };

    private static Task<DownloadClientActionResult> Remove(string app, Rig rig, string action)
    {
        var item = Client(app);
        return app switch
        {
            "qbittorrent" => new QbittorrentDownloadClient(() => rig.Handler).ExecuteActionAsync(item, action, Hash, CancellationToken.None),
            "transmission" => new TransmissionDownloadClient(new Factory(rig.Handler)).ExecuteActionAsync(item, action, Hash, CancellationToken.None),
            "deluge" => new DelugeDownloadClient(new Factory(rig.Handler)).ExecuteActionAsync(item, action, Hash, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(app))
        };
    }

    public static IEnumerable<object[]> EveryTorrentClientAndAction()
        => new[] { "qbittorrent", "transmission", "deluge" }.SelectMany(app => RemovingActions.Select(action => new object[] { app, action }));

    [Theory]
    [MemberData(nameof(EveryTorrentClientAndAction))]
    public async Task A_removal_of_a_download_the_client_has_is_done_once_it_is_gone(string app, string action)
    {
        var rig = RigFor(app);

        var result = await Remove(app, rig, action);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, rig.Deletes);
    }

    /// <summary>The suite's "the second removal reads as a failure": the client no longer has it, so nothing is sent and nothing is "done".</summary>
    [Theory]
    [MemberData(nameof(EveryTorrentClientAndAction))]
    public async Task A_second_removal_is_a_plain_failure_and_sends_no_delete(string app, string action)
    {
        var rig = RigFor(app);
        Assert.True((await Remove(app, rig, action)).Succeeded);

        var again = await Remove(app, rig, action);

        Assert.False(again.Succeeded, "the second removal read as done");
        Assert.Contains("has no download with that id", again.Message);
        Assert.Equal(1, rig.Deletes);
    }

    [Theory]
    [MemberData(nameof(EveryTorrentClientAndAction))]
    public async Task A_download_the_client_never_had_is_a_failure_that_sends_no_delete(string app, string action)
    {
        var rig = RigFor(app, has: false);

        var result = await Remove(app, rig, action);

        Assert.False(result.Succeeded);
        Assert.Equal(0, rig.Deletes);
    }

    [Theory]
    [MemberData(nameof(EveryTorrentClientAndAction))]
    public async Task A_client_that_still_has_the_download_after_saying_yes_is_a_failure(string app, string action)
    {
        var rig = RigFor(app, keepsItAfterDelete: true);

        var result = await Remove(app, rig, action);

        Assert.False(result.Succeeded);
        Assert.Contains("still has this download", result.Message);
    }

    [Theory]
    [MemberData(nameof(EveryTorrentClientAndAction))]
    public async Task A_client_that_cannot_be_asked_changes_nothing(string app, string action)
    {
        var rig = RigFor(app, listingFails: true);

        var result = await Remove(app, rig, action);

        Assert.False(result.Succeeded);
        Assert.Equal(0, rig.Deletes);
    }

    // ------------------------------------------------------------------ NZBGet

    private const int JobId = 28;

    /// <summary>
    /// A stand-in NZBGet holding the recorded finished folder job (NZBGet 26.3 history). Its history record names where the
    /// finished files are in <c>FinalDir</c> (or <c>DestDir</c> when <c>FinalDir</c> is empty); the delete commands answer
    /// as recorded and drop the record, and, as the real NZBGet does, leave a folder outside its own incomplete area where it is.
    /// </summary>
    private sealed class FakeNzbGet(string? finalDir, string? destDir = null)
    {
        private bool _has = true;

        public HttpMessageHandler Handler => RecordedReplies.Handler("nzbget",
            [RecordedReplies.Nzbget("idle"), RecordedReplies.Nzbget("unknown-id"), RecordedReplies.Nzbget("removed-with-data")],
            (_, body) =>
            {
                // GroupDelete and HistoryDelete (the plain delete) were not recorded: they answer as the recorded commands
                // of the same kind do, false for a job that is not in the queue and true for the history record.
                if (body.Contains("\"GroupDelete\"", StringComparison.Ordinal))
                {
                    return RecordedReplies.Nzbget("unknown-id").Find("GroupFinalDelete").Reply.ToResponse();
                }

                if (body.Contains("\"HistoryDelete\"", StringComparison.Ordinal))
                {
                    _has = false;
                    return RecordedReplies.Nzbget("removed-with-data").Find("HistoryFinalDelete").Reply.ToResponse();
                }

                if (body.Contains("HistoryFinalDelete", StringComparison.Ordinal))
                {
                    _has = false;
                }

                if (body.Contains("\"history\"", StringComparison.Ordinal))
                {
                    var edits = new List<(string, object?)> { ("result.0.FinalDir", finalDir ?? string.Empty) };
                    if (destDir is not null) edits.Add(("result.0.DestDir", destDir));
                    edits.Add(("result.0.Status", "SUCCESS/ALL"));
                    edits.Add(("result.0.MoveStatus", "SUCCESS"));
                    return _has
                        ? RecordedReplies.Json(RecordedReplies.Nzbget("finished-folder").Body("\"history\"", edits.ToArray()))
                        : RecordedReplies.Json(RecordedReplies.Nzbget("removed-with-data").Body("\"history\""));
                }

                if (body.Contains("\"listgroups\"", StringComparison.Ordinal))
                {
                    return RecordedReplies.Json(RecordedReplies.Nzbget("idle").Body("listgroups", ("result", new System.Text.Json.Nodes.JsonArray())));
                }

                return null;
            });
    }

    private static Task<DownloadClientActionResult> RemoveFromNzbGet(FakeNzbGet nzb, string action)
        => new NzbGetDownloadClient(new Factory(nzb.Handler), removalSettle: TimeSpan.Zero)
            .ExecuteActionAsync(Client("nzbget"), action, JobId.ToString(), CancellationToken.None);

    /// <summary>The suite's NZBGet failure: the job's folder and its .mkv were still in the completed folder 20 s later.</summary>
    [Theory]
    [InlineData(DownloadClientActions.DeleteWithData)]
    [InlineData(DownloadClientActions.RemoveRefusedCopy)]
    public async Task A_finished_nzbget_jobs_own_folder_goes_with_it_extras_included(string action)
    {
        using var scratch = new Scratch();
        var folder = Path.Combine(scratch.Path, "Completed", "Movies", "Recorded.Folder");
        Directory.CreateDirectory(Path.Combine(folder, "Sample"));
        File.WriteAllText(Path.Combine(folder, "Recorded.Folder.mkv"), "film");
        File.WriteAllText(Path.Combine(folder, "Recorded.Folder.nfo"), "nfo");
        File.WriteAllText(Path.Combine(folder, "Sample", "sample.mkv"), "sample");

        var result = await RemoveFromNzbGet(new FakeNzbGet(folder), action);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(Directory.Exists(folder), "the job's folder was left behind");
        Assert.Null(result.FilesNote);
        Assert.True(Directory.Exists(Path.Combine(scratch.Path, "Completed", "Movies")), "only the job's own folder goes");
    }

    /// <summary>When NZBGet leaves FinalDir empty the finished files are in DestDir, the folder it reports for the job.</summary>
    [Fact]
    public async Task A_finished_nzbget_job_with_no_final_folder_is_removed_from_its_destination_folder()
    {
        using var scratch = new Scratch();
        var folder = Path.Combine(scratch.Path, "Completed", "Movies", "Recorded.Folder");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Recorded.Folder.mkv"), "film");

        var result = await RemoveFromNzbGet(new FakeNzbGet(finalDir: null, destDir: folder), DownloadClientActions.DeleteWithData);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task A_plain_nzbget_delete_leaves_the_folder_alone()
    {
        using var scratch = new Scratch();
        var folder = Path.Combine(scratch.Path, "Completed", "Movies", "Recorded.Folder");
        Directory.CreateDirectory(folder);

        var result = await RemoveFromNzbGet(new FakeNzbGet(folder), DownloadClientActions.Delete);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(Directory.Exists(folder));
    }

    /// <summary>#1224's rule: a folder that is not named after the job (a category folder) is never wiped, and "deleted its files" is not claimed.</summary>
    [Fact]
    public async Task A_folder_that_is_not_the_jobs_own_is_left_alone_and_said_so()
    {
        using var scratch = new Scratch();
        var shared = Path.Combine(scratch.Path, "Completed", "Movies");
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "Another.Film.mkv"), "another film");

        var result = await RemoveFromNzbGet(new FakeNzbGet(shared), DownloadClientActions.DeleteWithData);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(Path.Combine(shared, "Another.Film.mkv")));
        Assert.NotNull(result.FilesNote);
    }

    [Fact]
    public async Task A_second_nzbget_removal_is_a_failure_and_deletes_nothing_more()
    {
        using var scratch = new Scratch();
        var folder = Path.Combine(scratch.Path, "Completed", "Movies", "Recorded.Folder");
        Directory.CreateDirectory(folder);
        var nzb = new FakeNzbGet(folder);
        Assert.True((await RemoveFromNzbGet(nzb, DownloadClientActions.RemoveRefusedCopy)).Succeeded);

        var again = await RemoveFromNzbGet(nzb, DownloadClientActions.RemoveRefusedCopy);

        Assert.False(again.Succeeded);
        Assert.Contains("has no download with that id", again.Message);
    }

    private sealed class Scratch : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deluno-remove-" + Guid.NewGuid().ToString("n"));

        public Scratch() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static DownloadClientItem Client(string protocol)
        => new("client-1", protocol, protocol, "localhost", 8080, null, "secret", "http://localhost:8080/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
