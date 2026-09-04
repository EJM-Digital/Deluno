using Deluno.Integrations.DownloadClients;
using Deluno.Worker.Services;

namespace Deluno.Worker.Tests.Services;

public sealed class NativeHistoryImportCandidateTests
{
    [Fact]
    public void Completed_native_history_is_an_import_candidate_when_queue_row_has_already_disappeared()
    {
        var history = History("native", "completed", "C:\\completed\\tv\\Episode.mp4");
        var candidates = WorkPlanner.GetImportCandidates(Overview([], [history]));

        var candidate = Assert.Single(candidates);
        Assert.Equal(DownloadQueueStatuses.Completed, candidate.Status);
        Assert.Equal(history.ExternalId, candidate.Id);
        Assert.Equal(history.SourcePath, candidate.SourcePath);
        Assert.Equal(history.SizeBytes, candidate.DownloadedBytes);
    }

    [Theory]
    [InlineData("dispatch-derived", "completed", "C:\\completed\\tv\\Episode.mp4")]
    [InlineData("native", "failed", "C:\\completed\\tv\\Episode.mp4")]
    [InlineData("native", "completed", null)]
    public void Unproven_or_unimportable_history_is_not_an_import_candidate(
        string historySource,
        string outcome,
        string? sourcePath)
    {
        var candidates = WorkPlanner.GetImportCandidates(Overview([], [History(historySource, outcome, sourcePath)]));

        Assert.Empty(candidates);
    }

    [Fact]
    public void Live_queue_row_wins_when_native_history_reports_the_same_source()
    {
        var queue = new DownloadQueueItem(
            "queue-id", "client-1", "SABnzbd", "sabnzbd", "tv", "Episode", "Episode.Release",
            "tv", DownloadQueueStatuses.Completed, 1, 0, 0, 2413, 2413, 0, "Indexer", null,
            DateTimeOffset.UnixEpoch, "C:\\completed\\tv\\Episode.mp4");

        var candidate = Assert.Single(WorkPlanner.GetImportCandidates(
            Overview([queue], [History("native", "completed", queue.SourcePath)])));

        Assert.Equal("queue-id", candidate.Id);
    }

    [Fact]
    public void Import_file_name_preserves_the_clients_real_container_extension()
    {
        var candidate = Assert.Single(WorkPlanner.GetImportCandidates(
            Overview([], [History("native", "completed", "C:\\completed\\tv\\Episode.mp4")])));

        Assert.Equal("Episode.Release.mp4", WorkPlanner.InferImportFileName(candidate));
    }

    /// <summary>
    /// A release name is indexer text, and a separator in it is not a
    /// separator Deluno should keep.
    ///
    /// <para><c>Path.GetInvalidFileNameChars()</c> answers for the host — on
    /// Linux it returns NUL and <c>/</c> only — so a backslash survived there
    /// and the container inferred a file name Windows would have cleaned. Both
    /// are asserted here so this cannot pass on one platform by accident.</para>
    /// </summary>
    [Theory]
    [InlineData(@"Episode\Release", "Episode.Release.mp4")]
    [InlineData("Episode/Release", "Episode.Release.mp4")]
    [InlineData("Episode Release", "Episode.Release.mp4")]
    public void A_separator_in_a_release_name_never_survives_into_a_file_name(
        string releaseName,
        string expected)
    {
        var queue = new DownloadQueueItem(
            "queue-id", "client-1", "SABnzbd", "sabnzbd", "tv", "Episode", releaseName,
            "tv", DownloadQueueStatuses.Completed, 1, 0, 0, 2413, 2413, 0, "Indexer", null,
            DateTimeOffset.UnixEpoch, @"C:\completed\tv\Episode.mp4");

        var candidate = Assert.Single(WorkPlanner.GetImportCandidates(
            Overview([queue], [History("native", "completed", queue.SourcePath)])));

        Assert.Equal(expected, WorkPlanner.InferImportFileName(candidate));
    }

    private static DownloadTelemetryOverview Overview(
        IReadOnlyList<DownloadQueueItem> queue,
        IReadOnlyList<DownloadClientHistoryItem> history)
        => new(
            EmptySummary(),
            [new DownloadClientTelemetrySnapshot(
                "client-1",
                "SABnzbd",
                "sabnzbd",
                "http://sabnzbd",
                "healthy",
                null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, false, true, "api-key"),
                EmptySummary(),
                queue,
                history,
                DateTimeOffset.UnixEpoch)],
            DateTimeOffset.UnixEpoch);

    private static DownloadClientHistoryItem History(string source, string outcome, string? sourcePath)
        => new(
            "native-id",
            "client-1",
            "SABnzbd",
            "sabnzbd",
            "tv",
            "Episode",
            "Episode.Release",
            "tv",
            outcome,
            "SABnzbd",
            2413,
            DateTimeOffset.UnixEpoch,
            outcome == "failed" ? "failed" : null,
            sourcePath,
            source,
            "native-external-id");

    private static DownloadTelemetrySummary EmptySummary()
        => new(0, 0, 0, 0, 0, 0, 0);
}
