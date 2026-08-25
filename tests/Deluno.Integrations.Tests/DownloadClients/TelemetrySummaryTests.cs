using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// `ProcessingCount` has always been a mixed bucket — post-processor waiting
/// and import work counted together. The dashboard rendered the whole thing
/// under "Importing", so a download held back for FileFlows was reported as
/// being imported. `WaitingForProcessorCount` reports the processor share so
/// the two can be told apart; these pin that split at its source.
/// </summary>
public sealed class TelemetrySummaryTests
{
    private sealed class TestClient : DownloadClientBase
    {
        public override string Protocol => "test";

        public override DownloadClientTelemetryCapabilities Capabilities => new(
            SupportsQueue: true,
            SupportsHistory: false,
            SupportsPauseResume: false,
            SupportsRemove: false,
            SupportsRecheck: false,
            SupportsImportPath: false,
            AuthMode: "none");

        public DownloadTelemetrySummary Summarise(IReadOnlyList<DownloadQueueItem> queue)
            => CreateSnapshot(
                new DownloadClientItem(
                    Id: "client-1",
                    Name: "Test",
                    Protocol: "test",
                    Host: "localhost",
                    Port: 8080,
                    Username: null,
                    Secret: null,
                    EndpointUrl: "http://localhost:8080",
                    MoviesCategory: null,
                    TvCategory: null,
                    CategoryTemplate: null,
                    Priority: 1,
                    IsEnabled: true,
                    HealthStatus: "healthy",
                    LastHealthMessage: null,
                    LastHealthFailureCategory: null,
                    LastHealthLatencyMs: null,
                    LastHealthTestUtc: null,
                    CreatedUtc: DateTimeOffset.UnixEpoch,
                    UpdatedUtc: DateTimeOffset.UnixEpoch),
                queue,
                DateTimeOffset.UnixEpoch,
                "healthy",
                null,
                []).Summary;

        public override Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
            => nativeStatus ?? DownloadQueueStatuses.Queued;
    }

    private static DownloadQueueItem Item(string id, string status) => new(
        Id: id,
        ClientId: "client-1",
        ClientName: "Test",
        Protocol: "test",
        MediaType: "movie",
        Title: "Title",
        ReleaseName: "Release",
        Category: "movies",
        Status: status,
        Progress: 100,
        SpeedMbps: 0,
        EtaSeconds: 0,
        SizeBytes: 0,
        DownloadedBytes: 0,
        Peers: 0,
        IndexerName: "indexer",
        ErrorMessage: null,
        AddedUtc: DateTimeOffset.UnixEpoch);

    [Fact]
    public void Waiting_for_a_processor_is_counted_apart_from_import_work()
    {
        var summary = new TestClient().Summarise([
            Item("a", DownloadQueueStatuses.WaitingForProcessor),
            Item("b", DownloadQueueStatuses.WaitingForProcessor),
            Item("c", DownloadQueueStatuses.ImportQueued)
        ]);

        // Still the whole bucket, so existing callers are unaffected…
        Assert.Equal(3, summary.ProcessingCount);
        // …but the processor share is now separable, which is what lets the
        // dashboard stop calling processor-blocked work "Importing".
        Assert.Equal(2, summary.WaitingForProcessorCount);
        Assert.Equal(1, summary.ProcessingCount - summary.WaitingForProcessorCount);
    }

    [Fact]
    public void An_install_with_no_processor_reports_none_waiting()
    {
        var summary = new TestClient().Summarise([
            Item("a", DownloadQueueStatuses.ImportQueued),
            Item("b", DownloadQueueStatuses.Downloading)
        ]);

        Assert.Equal(1, summary.ProcessingCount);
        Assert.Equal(0, summary.WaitingForProcessorCount);
    }

    [Fact]
    public void The_processor_share_never_exceeds_the_bucket_it_belongs_to()
    {
        var summary = new TestClient().Summarise([
            Item("a", DownloadQueueStatuses.WaitingForProcessor),
            Item("b", DownloadQueueStatuses.Downloading),
            Item("c", DownloadQueueStatuses.Queued)
        ]);

        Assert.True(summary.WaitingForProcessorCount <= summary.ProcessingCount);
        Assert.Equal(1, summary.ActiveCount);
        Assert.Equal(1, summary.QueuedCount);
    }
}
