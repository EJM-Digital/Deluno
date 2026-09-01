using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;

namespace Deluno.Persistence.Tests.Integrations;

public sealed class RepeatedDispatchImportIdentityTests
{
    private const string SourcePath = "C:\\completed\\Release";

    [Fact]
    public void Telemetry_does_not_label_a_reused_source_imported_from_an_older_dispatch()
    {
        var item = QueueItem();
        var oldJob = ImportJob("completed", "dispatch-old");
        var newDispatch = Dispatch("dispatch-new", DateTimeOffset.UnixEpoch.AddMinutes(2));

        Assert.False(DownloadClientTelemetryService.ShouldApplyImportJobState(item, oldJob, [newDispatch]));
        Assert.True(DownloadClientTelemetryService.ShouldApplyImportJobState(
            item,
            ImportJob("completed", "dispatch-new"),
            [newDispatch]));
    }

    private static JobQueueItem ImportJob(string status, string? dispatchId)
        => new(
            Id: $"job-{status}-{dispatchId}",
            JobType: "filesystem.import.execute",
            Source: "download-client",
            Status: status,
            PayloadJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                Preview = new { SourcePath },
                DispatchId = dispatchId
            }),
            Attempts: 1,
            CreatedUtc: DateTimeOffset.UnixEpoch,
            ScheduledUtc: DateTimeOffset.UnixEpoch,
            StartedUtc: null,
            CompletedUtc: null,
            LeasedUntilUtc: null,
            WorkerId: null,
            LastError: null,
            RelatedEntityType: "movie",
            RelatedEntityId: "movie-1",
            IdempotencyKey: null,
            DedupeKey: null,
            MaxAttempts: 3,
            LastAttemptUtc: null,
            NextAttemptUtc: null);

    private static DownloadQueueItem QueueItem()
        => new(
            Id: "queue-1",
            ClientId: "client-1",
            ClientName: "Client",
            Protocol: "qbittorrent",
            MediaType: "movies",
            Title: "Release",
            ReleaseName: "Release.2026.2160p",
            Category: "movies",
            Status: DownloadQueueStatuses.ImportReady,
            Progress: 100,
            SpeedMbps: 0,
            EtaSeconds: 0,
            SizeBytes: 100,
            DownloadedBytes: 100,
            Peers: 0,
            IndexerName: "Indexer",
            ErrorMessage: null,
            AddedUtc: DateTimeOffset.UnixEpoch,
            SourcePath: SourcePath);

    private static DownloadDispatchItem Dispatch(string id, DateTimeOffset createdUtc)
        => new(
            Id: id,
            LibraryId: "library-1",
            MediaType: "movies",
            EntityType: "movie",
            EntityId: "movie-1",
            ReleaseName: "Release.2026.2160p",
            IndexerName: "Indexer",
            DownloadClientId: "client-1",
            DownloadClientName: "Client",
            Status: "sent",
            NotesJson: null,
            CreatedUtc: createdUtc,
            GrabStatus: "succeeded",
            GrabAttemptedUtc: createdUtc,
            GrabResponseCode: 200,
            GrabMessage: "sent",
            GrabFailureCode: null,
            GrabResponseJson: null,
            DetectedUtc: null,
            TorrentHashOrItemId: null,
            DownloadedBytes: null,
            ImportStatus: null,
            ImportDetectedUtc: null,
            ImportCompletedUtc: null,
            ImportedFilePath: null,
            ImportFailureCode: null,
            ImportFailureMessage: null,
            CircuitOpenUntilUtc: null);
}
