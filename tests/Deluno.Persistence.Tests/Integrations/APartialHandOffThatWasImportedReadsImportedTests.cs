using Deluno.Platform.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// Golden path rc.30.10: a processor left one small extra out, Deluno imported the main video, and the hand-off was
/// settled as dismissed. The download stayed "Ready to import" in Transfers (#1182).
/// </summary>
public sealed class APartialHandOffThatWasImportedReadsImportedTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-09T00:00:00Z");
    private const string Release = "Nosferatu.1922.1080p.BluRay.x264-GRP";
    private const string SourcePath = "/downloads/Nosferatu.1922.1080p.BluRay.x264-GRP";
    private const string Extras = "Weir left one small extra file out (Gallery.mkv). The main video was delivered and imported.";

    [Theory]
    [InlineData("imported")]
    [InlineData("pending")]
    [InlineData(null)]
    public void A_dismissed_hand_off_for_a_title_that_holds_its_file_reads_Imported(string? importStatus)
    {
        var item = Assert.Single(Enrich(importStatus, "dismissed", entitiesWithFile: new HashSet<string> { "movie-1" }).Queue);

        Assert.Equal(DownloadQueueStatuses.Imported, item.Status);
    }

    [Theory]
    [InlineData("imported")]
    [InlineData("pending")]
    public void A_dismissed_hand_off_whose_import_job_finished_reads_Imported_even_when_the_title_lookup_is_empty(string importStatus)
    {
        var item = Assert.Single(Enrich(importStatus, "dismissed", entitiesWithFile: null, jobStatus: "completed").Queue);

        Assert.Equal(DownloadQueueStatuses.Imported, item.Status);
    }

    [Fact]
    public void A_completed_hand_off_with_the_extras_note_reads_Imported()
    {
        var item = Assert.Single(Enrich("imported", "completed", entitiesWithFile: new HashSet<string> { "movie-1" }, jobStatus: "completed").Queue);

        Assert.Equal(DownloadQueueStatuses.Imported, item.Status);
    }

    [Fact]
    public void The_Dashboard_importing_count_agrees_once_the_dismissed_import_reads_Imported()
    {
        var queue = Enrich("pending", "dismissed", entitiesWithFile: null, jobStatus: "completed").Queue;

        var summary = DownloadQueueSummary.Of(queue);

        Assert.Equal(0, summary.ImportReadyCount);
        Assert.Equal(0, summary.ProcessingCount);
    }

    private static DownloadClientTelemetrySnapshot Enrich(string? importStatus, string handoffStatus, IReadOnlySet<string>? entitiesWithFile, string? jobStatus = null)
    {
        var dispatch = new DownloadDispatchItem(
            Id: "dispatch-1", LibraryId: "library-1", MediaType: "movies", EntityType: "movie", EntityId: "movie-1",
            ReleaseName: Release, IndexerName: "Lab", DownloadClientId: "client-1", DownloadClientName: "Deluge",
            Status: "sent", NotesJson: null, CreatedUtc: Now.AddMinutes(-30), GrabStatus: "succeeded", GrabAttemptedUtc: Now,
            GrabResponseCode: 200, GrabMessage: null, GrabFailureCode: null, GrabResponseJson: null, DetectedUtc: null,
            TorrentHashOrItemId: "torrent-hash-1", DownloadedBytes: null, ImportStatus: importStatus, ImportDetectedUtc: null,
            ImportCompletedUtc: null, ImportedFilePath: null, ImportFailureCode: null, ImportFailureMessage: null,
            CircuitOpenUntilUtc: null, NextRetryEligibleUtc: null, AttemptCount: 1);
        var handoff = new ProcessorHandoffItem(
            Id: "handoff-1", LibraryId: "library-1", MediaType: "movies", ClientId: "client-1", QueueItemId: "torrent-hash-1",
            ReleaseName: Release, SourcePath: SourcePath, ProcessorName: "Weir", Status: handoffStatus,
            OutputPath: "/ready/Nosferatu.mkv", ImportJobId: "job-1", FailureMessage: Extras, CreatedUtc: Now.AddMinutes(-20), UpdatedUtc: Now);
        var jobs = jobStatus is null
            ? Array.Empty<JobQueueItem>()
            : [new JobQueueItem("job-1", "filesystem.import.execute", "processor", jobStatus, null, 1, Now, Now, Now, Now, null, null, null, null, null, null, null, 3, Now, null)];
        return DownloadClientTelemetryService.EnrichQueueImportState(
            new DownloadClientTelemetrySnapshot(
                "client-1", "Deluge", "deluge", "http://localhost:8112/", "healthy", null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "session"),
                new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
                [new DownloadQueueItem(
                    "torrent-hash-1", "client-1", "Deluge", "deluge", "movies", "Nosferatu", Release, "movies",
                    DownloadQueueStatuses.ImportReady, 1.0, 0, 0, 100, 100, 0, "Lab", null, Now, SourcePath: SourcePath)],
                [],
                Now),
            libraries: [],
            dispatches: [dispatch],
            settledDispatches: [dispatch],
            importJobs: jobs,
            handoffs: [handoff],
            routeCategoriesByLibrary: new Dictionary<string, IReadOnlyDictionary<string, string>>(),
            entitiesWithFile: entitiesWithFile);
    }
}
