using Deluno.Platform.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// Live on a clean install: a film whose cleaned file could not be read was listed in Transfers as "In library ...
/// Imported" while Activity and Needs attention said "Import failed". The import job had run to its end, so the
/// row read it as imported; the dispatch holds the real outcome.
/// </summary>
public sealed class AnImportThatFailedIsNeverShownAsImportedTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T00:00:00Z");
    private const string Release = "Reject.Film.2020.1080p.BluRay.x264-GRP";
    private const string Reason = "The processed file will not open or play. The file can't be read as video; it may be incomplete or damaged.";

    [Fact]
    public void A_finished_import_job_for_a_refused_import_reads_Import_failed_with_the_reason()
    {
        var item = Assert.Single(Enrich(Dispatch("failed", "mediaProbeRejected", Reason), handoff: Handoff(), jobStatus: "completed").Queue);

        Assert.Equal(DownloadQueueStatuses.ImportFailed, item.Status);
        Assert.Equal(Reason, item.ErrorMessage);
    }

    [Fact]
    public void A_finished_import_job_found_by_the_download_path_also_reads_Import_failed()
    {
        var item = Assert.Single(Enrich(Dispatch("failed", "mediaProbeRejected", Reason), handoff: null, jobStatus: "completed", jobForSource: true).Queue);

        Assert.Equal(DownloadQueueStatuses.ImportFailed, item.Status);
    }

    [Fact]
    public void An_import_that_worked_still_reads_Imported()
    {
        var item = Assert.Single(Enrich(Dispatch("imported", null, null), handoff: Handoff(), jobStatus: "completed").Queue);

        Assert.Equal(DownloadQueueStatuses.Imported, item.Status);
    }

    [Fact]
    public void A_failure_Deluno_handled_itself_is_not_shown_as_a_failed_import()
    {
        var item = Assert.Single(Enrich(Dispatch("failed", "health-remediation-applied", "Replaced."), handoff: Handoff(), jobStatus: "completed").Queue);

        Assert.Equal(DownloadQueueStatuses.Imported, item.Status);
    }

    [Fact]
    public void A_refusal_that_has_since_been_archived_still_reads_Import_failed()
    {
        var item = Assert.Single(Enrich(Dispatch("failed", "likelySample", "The detected runtime is under two minutes, so Deluno is treating this as a likely sample."),
            handoff: Handoff(), jobStatus: "completed", activeDispatches: false).Queue);

        Assert.Equal(DownloadQueueStatuses.ImportFailed, item.Status);
    }

    [Fact]
    public void A_refusal_never_reads_Imported_even_when_the_title_holds_a_file_and_the_hand_off_is_done()
    {
        var handoff = Handoff() with { ImportJobId = null };
        var item = Assert.Single(Enrich(Dispatch("failed", "likelySample", "Likely a sample."), handoff: handoff, jobStatus: "completed",
            entitiesWithFile: new HashSet<string> { "movie-1" }).Queue);

        Assert.Equal(DownloadQueueStatuses.ImportFailed, item.Status);
    }

    private static DownloadClientTelemetrySnapshot Enrich(DownloadDispatchItem dispatch, ProcessorHandoffItem? handoff, string jobStatus, bool jobForSource = false, bool activeDispatches = true, IReadOnlySet<string>? entitiesWithFile = null)
        => DownloadClientTelemetryService.EnrichQueueImportState(
            new DownloadClientTelemetrySnapshot(
                "client-1", "Deluge", "deluge", "http://localhost:8112/", "healthy", null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "session"),
                new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
                [QueueItem()],
                [],
                Now),
            libraries: [],
            dispatches: activeDispatches ? [dispatch] : [],
            settledDispatches: [dispatch],
            importJobs: [Job(jobStatus, jobForSource ? SourcePath : null)],
            handoffs: handoff is null ? [] : [handoff],
            routeCategoriesByLibrary: new Dictionary<string, IReadOnlyDictionary<string, string>>(),
            entitiesWithFile: entitiesWithFile);

    private const string SourcePath = "/downloads/Reject.Film.2020.1080p.BluRay.x264-GRP";

    private static DownloadQueueItem QueueItem()
        => new(
            "torrent-hash-1", "client-1", "Deluge", "deluge", "movies", "Reject Film", Release, "movies",
            DownloadQueueStatuses.ImportReady, 1.0, 0, 0, 100, 100, 0, "Lab Torznab", null, Now,
            SourcePath: SourcePath);

    private static JobQueueItem Job(string status, string? sourcePath)
        => new("job-1", "filesystem.import.execute", "processor-watch", status,
            sourcePath is null ? null : System.Text.Json.JsonSerializer.Serialize(new { preview = new { sourcePath }, dispatchId = "dispatch-1" }),
            1, Now, Now, Now, Now, null, null, null, null, null, null, null, 3, Now, null);

    private static ProcessorHandoffItem Handoff()
        => new(
            Id: "handoff-1", LibraryId: "library-1", MediaType: "movies", ClientId: "client-1", QueueItemId: "torrent-hash-1",
            ReleaseName: Release, SourcePath: SourcePath, ProcessorName: "Weir", Status: "completed",
            OutputPath: "/ready/Reject.Film.mkv", ImportJobId: "job-1", FailureMessage: null, CreatedUtc: Now, UpdatedUtc: Now);

    private static DownloadDispatchItem Dispatch(string importStatus, string? failureCode, string? failureMessage)
        => new(
            Id: "dispatch-1", LibraryId: "library-1", MediaType: "movies", EntityType: "movie", EntityId: "movie-1",
            ReleaseName: Release, IndexerName: "Lab Torznab", DownloadClientId: "client-1", DownloadClientName: "Deluge",
            Status: "sent", NotesJson: null, CreatedUtc: Now.AddMinutes(-30), GrabStatus: "succeeded", GrabAttemptedUtc: Now,
            GrabResponseCode: 200, GrabMessage: null, GrabFailureCode: null, GrabResponseJson: null, DetectedUtc: null,
            TorrentHashOrItemId: "torrent-hash-1", DownloadedBytes: null, ImportStatus: importStatus, ImportDetectedUtc: null,
            ImportCompletedUtc: null, ImportedFilePath: null, ImportFailureCode: failureCode, ImportFailureMessage: failureMessage,
            CircuitOpenUntilUtc: null, NextRetryEligibleUtc: null, AttemptCount: 1);
}
