using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using Deluno.Integrations.DownloadClients;
using Deluno.Platform.Contracts;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// Scenario suite on rc.30.22 (#1210): with the files of a finished SABnzbd or NZBGet job deleted, Deluno never said
/// "Files are gone" and SABnzbd's job was still handed to Weir. Both clients list a finished job in their history only,
/// never in the queue, so the queue check never saw it; and SABnzbd's source is a file inside the job's own folder,
/// which is deleted with the file, so the check's "the folder is there" test failed too. The two paths are the ones
/// the run recorded (vanish-before-sabnzbd-row.json, vanish-before-nzbget-row.json), in the shape CI's Linux reads.
/// </summary>
public sealed class AUsenetDownloadWhoseFilesVanishTests
{
    private const string SabRelease = "Gpgoneabtu43.2020.1080p.WEB-DL.x264-GOLDEN";
    private const string NzbgetRelease = "Gpgoneabcs44.2020.1080p.WEB-DL.x264-GOLDEN";
    private const string Category = "/downloads/completed/movies";
    private static readonly DateTimeOffset Done = DateTimeOffset.Parse("2026-10-11T13:14:50Z");

    private static bool OnlyTheCategoryFolderIsThere(string path) => path.Replace((char)92, (char)47).TrimEnd((char)47) == Category;

    [Fact]
    public void A_file_inside_a_job_folder_that_was_deleted_with_it_is_gone()
    {
        Assert.True(DownloadFilesGone.AreGone($"{Category}/{SabRelease}/{SabRelease}.mkv", OnlyTheCategoryFolderIsThere));
        Assert.True(DownloadFilesGone.AreGone($"{Category}/{NzbgetRelease}", OnlyTheCategoryFolderIsThere));
    }

    [Fact]
    public void A_path_whose_category_folder_cannot_be_seen_either_is_not_called_gone()
    {
        Assert.False(DownloadFilesGone.AreGone($"{Category}/{SabRelease}/{SabRelease}.mkv", _ => false));
        Assert.False(DownloadFilesGone.AreGone($"/mnt/other-machine/{SabRelease}/{SabRelease}.mkv", path => path.Replace((char)92, (char)47) == "/mnt"));
    }

    [Theory]
    [InlineData("sabnzbd", "b185140c-586b-4cad-90ed-47c4ed3339d0", SabRelease, Category + "/" + SabRelease + "/" + SabRelease + ".mkv")]
    [InlineData("nzbget", "15", NzbgetRelease, Category + "/" + NzbgetRelease)]
    public void A_finished_usenet_job_listed_in_history_whose_files_vanish_reads_Files_are_gone(string protocol, string id, string release, string sourcePath)
    {
        var result = DownloadClientTelemetryService.MarkDownloadsWhoseFilesAreGone(
            UsenetHistoryOnly(protocol, id, release, sourcePath), OnlyTheCategoryFolderIsThere, [], []);

        var row = Assert.Single(result.Queue);
        Assert.Equal(DownloadQueueStatuses.FilesGone, row.Status);
        Assert.Equal(id, row.Id);
        Assert.Equal(release, row.ReleaseName);
        Assert.Equal(DownloadFilesGone.Message, row.ErrorMessage);
    }

    [Fact]
    public void A_finished_usenet_job_whose_files_are_there_or_unseen_adds_no_row()
    {
        var snapshot = UsenetHistoryOnly("sabnzbd", "nzo-1", SabRelease, $"{Category}/{SabRelease}/{SabRelease}.mkv");

        Assert.Empty(DownloadClientTelemetryService.MarkDownloadsWhoseFilesAreGone(snapshot, _ => true, [], []).Queue);
        Assert.Empty(DownloadClientTelemetryService.MarkDownloadsWhoseFilesAreGone(snapshot, _ => false, [], []).Queue);
    }

    [Theory]
    [InlineData("submitted", true)]
    [InlineData("waiting", true)]
    [InlineData("source-gone", true)]
    [InlineData("completed", false)]
    public void A_job_waiting_at_the_processor_is_flagged_but_one_the_processor_finished_is_not(string handoffStatus, bool flagged)
    {
        var snapshot = UsenetHistoryOnly("sabnzbd", "nzo-1", SabRelease, $"{Category}/{SabRelease}/{SabRelease}.mkv");
        var handoff = new ProcessorHandoffItem(
            "handoff-1", "library-1", "movies", "client-1", "nzo-1", SabRelease, $"{Category}/{SabRelease}/{SabRelease}.mkv", "Weir",
            handoffStatus, handoffStatus == "completed" ? "/ready/x.mkv" : null, handoffStatus == "completed" ? "job-1" : null, null, Done, Done);

        var result = DownloadClientTelemetryService.MarkDownloadsWhoseFilesAreGone(snapshot, OnlyTheCategoryFolderIsThere, [handoff], []);

        Assert.Equal(flagged ? 1 : 0, result.Queue.Count);
    }

    [Fact]
    public void A_job_the_queue_already_lists_is_not_listed_twice()
    {
        var path = $"{Category}/{SabRelease}/{SabRelease}.mkv";
        var snapshot = UsenetHistoryOnly("sabnzbd", "nzo-1", SabRelease, path);
        snapshot = snapshot with
        {
            Queue = [new DownloadQueueItem("nzo-1", "client-1", "SABnzbd", "sabnzbd", "movies", "x", SabRelease, "movies",
                DownloadQueueStatuses.Completed, 100, 0, 0, 1, 1, 0, "SABnzbd", null, Done, SourcePath: path)]
        };

        var result = DownloadClientTelemetryService.MarkDownloadsWhoseFilesAreGone(snapshot, OnlyTheCategoryFolderIsThere, [], []);

        Assert.Equal(DownloadQueueStatuses.FilesGone, Assert.Single(result.Queue).Status);
    }

    private sealed class RecordedHttp(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler, IHttpClientFactory
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => answer(request, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
    }

    private static DownloadClientItem ClientItem(string protocol)
        => new("client-1", protocol, protocol, "localhost", 1234, "user", "secret", "http://localhost:1234/", false, null,
            "Movies", "TV", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    /// <summary>
    /// The same thing on what the clients really said (SABnzbd 5.1.3 and NZBGet 26.3, tests/fixtures/recorded: a finished job, with only
    /// the place it says its files are changed to a real folder on this disk) and a real file check: with the job's own folder gone
    /// and the category folder still there, the job reads "Files are gone", is not offered to the processor, and the words are the
    /// ones the Transfers row shows. With the files there, it reads as finished.
    /// </summary>
    [Theory]
    [InlineData("sabnzbd")]
    [InlineData("nzbget")]
    public async Task A_recorded_finished_job_whose_folder_was_deleted_from_a_real_download_area_reads_Files_are_gone_and_is_not_handed_on(string protocol)
    {
        var category = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "deluno-files-gone-" + Guid.NewGuid().ToString("n"), "completed", "movies")).FullName;
        try
        {
            const string job = "Recorded.Vanishing.Film.2020.1080p.WEB-DL.x264-GOLDEN";
            var jobFolder = Path.Combine(category, job);
            var finishedFile = Path.Combine(jobFolder, job + ".mkv");

            async Task<IReadOnlyList<DownloadClientHistoryItem>> HistoryAsync()
            {
                if (protocol == "sabnzbd")
                {
                    var history = RecordedReplies.Sabnzbd("finished-folder").Body("mode=history&output", ("history.slots.0.storage", finishedFile), ("history.slots.0.name", job), ("history.slots.0.nzb_name", job + ".nzb"));
                    var empty = RecordedReplies.Sabnzbd("removed-without-data").Body("mode=queue&output", ("queue.slots", new System.Text.Json.Nodes.JsonArray()));
                    var handler = new RecordedHttp((request, _) => RecordedReplies.Json(request.RequestUri!.Query.Contains("mode=history", StringComparison.Ordinal) ? history : empty));
                    return await new SabnzbdDownloadClient(handler).GetHistoryAsync(ClientItem(protocol), DateTimeOffset.UtcNow, CancellationToken.None);
                }

                var nzbget = RecordedReplies.Nzbget("finished-single-file").Body(
                    "history\"", ("^result", 1), ("result.0.Status", "SUCCESS/UNPACK"), ("result.0.Category", "Movies"),
                    ("result.0.NZBName", job), ("result.0.Name", job), ("result.0.DestDir", jobFolder));
                return await new NzbGetDownloadClient(new RecordedHttp((_, _) => RecordedReplies.Json(nzbget))).GetHistoryAsync(ClientItem(protocol), DateTimeOffset.UtcNow, CancellationToken.None);
            }

            DownloadClientTelemetrySnapshot Snapshot(IReadOnlyList<DownloadClientHistoryItem> history)
                => new("client-1", protocol, protocol, "http://localhost:1234/", "healthy", null,
                    new DownloadClientTelemetryCapabilities(true, true, true, true, false, true, "apikey"),
                    new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0), [], history, Done);

            // The job folder is gone: the recorded history names a path that is not on this disk, though the category folder is.
            var history = await HistoryAsync();
            var named = history.First(item => item.ReleaseName == job);
            history = [named];
            Assert.StartsWith(category, named.SourcePath!, StringComparison.OrdinalIgnoreCase);
            var gone = DownloadClientTelemetryService.MarkDownloadsWhoseFilesAreGone(Snapshot(history), (path => File.Exists(path) || Directory.Exists(path)), [], []);
            var row = Assert.Single(gone.Queue);
            Assert.Equal(DownloadQueueStatuses.FilesGone, row.Status);
            Assert.Equal(DownloadFilesGone.Message, row.ErrorMessage);
            Assert.False(DownloadFilesGone.ExpectsItsFiles(row.Status), "a download whose files are gone is not one the processor is offered");

            // The folder is there again: the same recorded job is an ordinary finished download.
            Directory.CreateDirectory(jobFolder);
            await File.WriteAllBytesAsync(finishedFile, new byte[1024]);
            var there = DownloadClientTelemetryService.MarkDownloadsWhoseFilesAreGone(Snapshot([(await HistoryAsync()).First(item => item.ReleaseName == job)]), (path => File.Exists(path) || Directory.Exists(path)), [], []);
            Assert.Empty(there.Queue);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(category))!, recursive: true);
        }
    }

    private static DownloadClientTelemetrySnapshot UsenetHistoryOnly(string protocol, string id, string release, string sourcePath)
        => new(
            "client-1", protocol, protocol, "http://localhost:8080/", "healthy", null,
            new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "apikey"),
            new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
            [],
            [new DownloadClientHistoryItem(
                id, "client-1", protocol, protocol, "movies", release.Replace('.', ' '), release, "movies",
                "completed", protocol, 99462355, Done, null, sourcePath, "native", id)],
            Done);
}
