using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Libraries.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// Inside Out 2 on the rig (#1496): qBittorrent still read the finished download as stalled, so the status was never
/// rewritten to waiting-for-processor and the Dashboard drew it as importing. The row now carries the planner's own
/// rule (<see cref="ImportWorkflows.HandsToProcessor"/>) whatever status the client reports.
/// </summary>
public sealed class AFinishedDownloadSaysWhetherItsLibraryUsesAProcessorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T04:50:34Z");
    private const string Release = "Inside.Out.2.2024.iTA.ENG.Bluray.1080p.x264-Dr4gon";

    [Theory]
    [InlineData("stalled")]
    [InlineData("downloading")]
    [InlineData("completed")]
    public void A_library_that_refines_before_importing_marks_the_row_whatever_the_client_calls_it(string status)
    {
        Assert.True(Enrich(status, ImportWorkflows.RefineBeforeImport)?.HandsToProcessor);
    }

    [Theory]
    [InlineData("stalled")]
    [InlineData("completed")]
    public void A_library_that_imports_directly_does_not(string status)
    {
        Assert.False(Enrich(status, "direct")?.HandsToProcessor);
    }

    [Fact]
    public void A_download_with_no_library_says_nothing()
    {
        Assert.Null(Enrich("stalled", null)?.HandsToProcessor);
    }

    private static DownloadQueueItem? Enrich(string status, string? workflow)
    {
        var dispatch = new DownloadDispatchItem(
            Id: "dispatch-1", LibraryId: "lib-movies", MediaType: "movies", EntityType: "movie", EntityId: "movie-1",
            ReleaseName: Release, IndexerName: "Lab", DownloadClientId: "client-1", DownloadClientName: "qBittorrent",
            Status: "sent", NotesJson: null, CreatedUtc: Now.AddMinutes(-30), GrabStatus: "succeeded", GrabAttemptedUtc: Now,
            GrabResponseCode: 200, GrabMessage: null, GrabFailureCode: null, GrabResponseJson: null, DetectedUtc: null,
            TorrentHashOrItemId: "hash-1", DownloadedBytes: null, ImportStatus: null, ImportDetectedUtc: null,
            ImportCompletedUtc: null, ImportedFilePath: null, ImportFailureCode: null, ImportFailureMessage: null,
            CircuitOpenUntilUtc: null, NextRetryEligibleUtc: null, AttemptCount: 1);
        var libraries = workflow is null
            ? Array.Empty<LibraryItem>()
            : [new LibraryItem("lib-movies", "Movies", "movies", "", "/movies", null, null, null, null, true, true, workflow, null, null, 60, "hold", true, true, true, 6, 12, 50, null, null, "active", false, null, null, Now, Now)];
        var snapshot = DownloadClientTelemetryService.EnrichQueueImportState(
            new DownloadClientTelemetrySnapshot(
                "client-1", "qBittorrent", "qbittorrent", "http://localhost:8080/", "healthy", null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "session"),
                new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
                [new DownloadQueueItem(
                    "hash-1", "client-1", "qBittorrent", "torrent", "movies", "Inside Out 2", Release, "movies",
                    status, 100, 0, 0, 100, 100, 0, "Lab", null, Now, SourcePath: "/downloads/inside-out-2")],
                [],
                Now),
            libraries: libraries,
            dispatches: [dispatch],
            settledDispatches: [dispatch],
            importJobs: [],
            handoffs: [],
            routeCategoriesByLibrary: new Dictionary<string, IReadOnlyDictionary<string, string>>(),
            entitiesWithFile: null);
        return snapshot.Queue.SingleOrDefault();
    }
}
