using Deluno.Contracts;
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
