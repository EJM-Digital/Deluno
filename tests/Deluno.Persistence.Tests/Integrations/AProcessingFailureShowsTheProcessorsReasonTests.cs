using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Libraries.Contracts;
using Deluno.Platform.Contracts;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// Live on the rig (#606, #616): Weir refused a hand-off with a clear reason
/// - "The hand-off names '...', which is not inside Weir's watched folder
/// '...'. Point the..." - and the activity feed carried it, but the queue row
/// and its health finding did not: the client itself never saw a failure (it
/// downloaded the file fine), so the queue item's own ErrorMessage stayed
/// empty, and the generic "the processor did not produce an import-ready
/// output" fallback was all there was to read.
///
/// <para>The hand-off record is the only place the processor's real reason
/// lives. <see cref="DownloadClientTelemetryService.EnrichQueueImportState"/>
/// already read it to decide the row's Status (<see
/// cref="ProcessorHandoffQueueStatusTests"/> covers that); these prove it now
/// also carries the reason into ErrorMessage, which is what the row badge and
/// <c>DownloadHealthEvaluator</c>'s Evidence field both read.</para>
/// </summary>
public sealed class AProcessingFailureShowsTheProcessorsReasonTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-24T00:00:00Z");

    private const string RefusalReason =
        "The processor refused the hand-off with HTTP 400: The hand-off names 'C:\\Deluno\\Downloads-Incomplete\\Deluge\\house.on.haunted.hill.1959', "
        + "which is not inside Weir's watched folder 'C:\\Deluno\\Downloads-Complete\\Movies'. Point the client to save inside that folder, or point Weir at this one.";

    [Fact]
    public void A_processing_failure_carries_the_processors_own_reason_into_the_queue_row()
    {
        var snapshot = Enrich(Handoff("failed", RefusalReason));

        var item = Assert.Single(snapshot.Queue);
        Assert.Equal(DownloadQueueStatuses.ProcessingFailed, item.Status);
        Assert.Equal(RefusalReason, item.ErrorMessage);
    }

    [Fact]
    public void A_client_reported_error_is_not_overwritten_by_the_handoffs_reason()
    {
        var withClientError = QueueItem() with { ErrorMessage = "Torrent Error" };
        var snapshot = Enrich(Handoff("failed", RefusalReason), withClientError);

        var item = Assert.Single(snapshot.Queue);
        Assert.Equal("Torrent Error", item.ErrorMessage);
    }

    [Fact]
    public void A_hand_off_still_with_the_processor_carries_no_error_at_all()
    {
        var snapshot = Enrich(Handoff("waiting", null));

        var item = Assert.Single(snapshot.Queue);
        Assert.Equal(DownloadQueueStatuses.WaitingForProcessor, item.Status);
        Assert.Null(item.ErrorMessage);
    }

    private static DownloadClientTelemetrySnapshot Enrich(ProcessorHandoffItem handoff, DownloadQueueItem? item = null)
        => DownloadClientTelemetryService.EnrichQueueImportState(
            new DownloadClientTelemetrySnapshot(
                "client-1",
                "Deluge",
                "deluge",
                "http://localhost:8112/",
                "healthy",
                null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "session"),
                new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
                [item ?? QueueItem()],
                [],
                Now),
            libraries: [],
            dispatches: [],
            settledDispatches: [],
            importJobs: [],
            handoffs: [handoff],
            routeCategoriesByLibrary: new Dictionary<string, IReadOnlyDictionary<string, string>>());

    private static DownloadQueueItem QueueItem()
        => new(
            "torrent-hash-1",
            "client-1",
            "Deluge",
            "deluge",
            "movies",
            "House on Haunted Hill",
            "House.on.Haunted.Hill.1959.1080p.BluRay.x264-AMIABLE",
            "movies",
            DownloadQueueStatuses.ImportReady,
            1.0,
            0,
            0,
            100,
            100,
            0,
            "Lab Torznab",
            null,
            Now,
            SourcePath: @"C:\Deluno\Downloads-Complete\Deluge\House.on.Haunted.Hill.1959.1080p.BluRay.x264-AMIABLE");

    private static ProcessorHandoffItem Handoff(string status, string? failureMessage)
        => new(
            Id: "handoff-1",
            LibraryId: "library-1",
            MediaType: "movies",
            ClientId: "client-1",
            QueueItemId: "torrent-hash-1",
            ReleaseName: "House.on.Haunted.Hill.1959.1080p.BluRay.x264-AMIABLE",
            SourcePath: @"C:\Deluno\Downloads-Complete\Deluge\House.on.Haunted.Hill.1959.1080p.BluRay.x264-AMIABLE",
            ProcessorName: "Weir",
            Status: status,
            OutputPath: null,
            ImportJobId: null,
            FailureMessage: failureMessage,
            CreatedUtc: Now,
            UpdatedUtc: Now);
}
