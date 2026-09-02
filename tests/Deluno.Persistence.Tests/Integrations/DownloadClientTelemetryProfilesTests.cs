using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Jobs.Contracts;
using Deluno.Contracts;

namespace Deluno.Persistence.Tests.Integrations;

public sealed class DownloadClientTelemetryProfilesTests
{
    [Theory]
    [InlineData("qbittorrent", false, true, true, "form")]
    [InlineData("sabnzbd", true, true, false, "api-key")]
    [InlineData("nzbget", true, true, false, "basic")]
    [InlineData("transmission", false, true, true, "basic")]
    [InlineData("deluge", false, true, true, "password")]
    [InlineData("utorrent", false, false, true, "basic-token")]
    public void ResolveCapabilities_ReturnsExpectedProtocolSupport(
        string protocol,
        bool supportsHistory,
        bool supportsImportPath,
        bool supportsRecheck,
        string authMode)
    {
        Assert.True(Registry().TryGet(protocol, out var client));
        var capabilities = client.Capabilities;

        Assert.True(capabilities.SupportsQueue);
        Assert.Equal(supportsHistory, capabilities.SupportsHistory);
        Assert.True(capabilities.SupportsPauseResume);
        Assert.True(capabilities.SupportsRemove);
        Assert.Equal(supportsRecheck, capabilities.SupportsRecheck);
        Assert.Equal(supportsImportPath, capabilities.SupportsImportPath);
        Assert.Equal(authMode, capabilities.AuthMode);
    }

    [Fact]
    public void Registry_rejects_unknown_protocols_and_lists_supported_protocols()
    {
        var registry = Registry();

        Assert.False(registry.TryGet("custom", out _));
        Assert.False(registry.TryGet("nonsense", out _));
        Assert.Equal(["deluge", "nzbget", "qbittorrent", "sabnzbd", "transmission", "utorrent"], registry.KnownProtocols);
    }

    [Fact]
    public void Dispatch_history_uses_the_latest_import_state_and_client_identity()
    {
        var createdUtc = DateTimeOffset.Parse("2026-08-20T10:00:00Z");
        var importedUtc = createdUtc.AddMinutes(20);
        var dispatch = new DownloadDispatchItem(
            Id: "dispatch-1",
            LibraryId: "library-1",
            MediaType: "movies",
            EntityType: "movie",
            EntityId: "movie-1",
            ReleaseName: "Example.Movie.2026.1080p.WEB",
            IndexerName: "Fixture Indexer",
            DownloadClientId: "client-1",
            DownloadClientName: "Fixture Client",
            Status: "sent",
            NotesJson: null,
            CreatedUtc: createdUtc,
            GrabStatus: "succeeded",
            GrabAttemptedUtc: createdUtc.AddMinutes(1),
            GrabResponseCode: 200,
            GrabMessage: null,
            GrabFailureCode: null,
            GrabResponseJson: null,
            DetectedUtc: createdUtc.AddMinutes(5),
            TorrentHashOrItemId: "client-item-1",
            DownloadedBytes: 987654,
            ImportStatus: "imported",
            ImportDetectedUtc: createdUtc.AddMinutes(19),
            ImportCompletedUtc: importedUtc,
            ImportedFilePath: @"C:\Library\Movies\Example.mkv",
            ImportFailureCode: null,
            ImportFailureMessage: null,
            CircuitOpenUntilUtc: null,
            NextRetryEligibleUtc: null,
            AttemptCount: 1);

        var history = DownloadClientTelemetryService.CreateDispatchHistoryItem(
            "client-1", "Fixture Client", "qbittorrent", dispatch, createdUtc.AddHours(1));

        Assert.Equal(DownloadQueueStatuses.Imported, history.Outcome);
        Assert.Equal(importedUtc, history.CompletedUtc);
        Assert.Equal(987654, history.SizeBytes);
        Assert.Equal("client-item-1", history.ExternalId);
        Assert.Null(history.ErrorMessage);
        Assert.Null(history.Failure);
    }

    [Fact]
    public void Dispatch_history_attributes_import_failures_to_import_not_grab()
    {
        var dispatch = new DownloadDispatchItem(
            Id: "dispatch-2",
            LibraryId: "library-1",
            MediaType: "tv",
            EntityType: "episode",
            EntityId: "episode-1",
            ReleaseName: "Example.Show.S01E01.1080p.WEB",
            IndexerName: "Fixture Indexer",
            DownloadClientId: "client-1",
            DownloadClientName: "Fixture Client",
            Status: "sent",
            NotesJson: null,
            CreatedUtc: DateTimeOffset.Parse("2026-08-20T10:00:00Z"),
            GrabStatus: "succeeded",
            GrabAttemptedUtc: DateTimeOffset.Parse("2026-08-20T10:01:00Z"),
            GrabResponseCode: 200,
            GrabMessage: "Accepted by client",
            GrabFailureCode: "grab-failed",
            GrabResponseJson: null,
            DetectedUtc: DateTimeOffset.Parse("2026-08-20T10:05:00Z"),
            TorrentHashOrItemId: "client-item-2",
            DownloadedBytes: 123,
            ImportStatus: "failed",
            ImportDetectedUtc: DateTimeOffset.Parse("2026-08-20T10:10:00Z"),
            ImportCompletedUtc: DateTimeOffset.Parse("2026-08-20T10:11:00Z"),
            ImportedFilePath: null,
            ImportFailureCode: "import-no-match",
            ImportFailureMessage: "The refined file did not match the episode.",
            CircuitOpenUntilUtc: null,
            NextRetryEligibleUtc: null,
            AttemptCount: 1);

        var history = DownloadClientTelemetryService.CreateDispatchHistoryItem(
            "client-1", "Fixture Client", "sabnzbd", dispatch, DateTimeOffset.UtcNow);

        Assert.Equal("failed", history.Outcome);
        Assert.Equal("The refined file did not match the episode.", history.ErrorMessage);
        Assert.NotNull(history.Failure);
        Assert.Equal("import", history.Failure!.Operation);
        Assert.Equal("import-no-match", history.Failure.Code);
    }

    [Fact]
    public void Dispatch_history_deduplicates_against_native_external_id_and_keeps_unmatched_trace_rows()
    {
        var capturedUtc = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
        var nativeHistory = new DownloadClientHistoryItem(
            Id: "native-row-1",
            ClientId: "client-1",
            ClientName: "Fixture Client",
            Protocol: "sabnzbd",
            MediaType: "movies",
            Title: "Example",
            ReleaseName: "Example.Movie.2026.1080p.WEB",
            Category: "movies",
            Outcome: DownloadQueueStatuses.Imported,
            IndexerName: "Fixture Indexer",
            SizeBytes: 123,
            CompletedUtc: capturedUtc,
            ErrorMessage: null,
            HistorySource: "native",
            ExternalId: "client-item-1");
        var snapshot = new DownloadClientTelemetrySnapshot(
            ClientId: "client-1",
            ClientName: "Fixture Client",
            Protocol: "sabnzbd",
            EndpointUrl: "http://sabnzbd.test",
            HealthStatus: "healthy",
            LastHealthMessage: null,
            Capabilities: new(true, true, true, true, false, false, "api-key"),
            Summary: new(0, 0, 0, 0, 0, 0, 0),
            Queue: [],
            History: [nativeHistory],
            CapturedUtc: capturedUtc);
        var matchingDispatch = CreateDispatch("dispatch-matching", "client-item-1", capturedUtc);
        var unmatchedDispatch = CreateDispatch("dispatch-unmatched", "client-item-2", capturedUtc.AddMinutes(-5));

        var merged = DownloadClientTelemetryService.EnrichWithDispatchHistory(
            snapshot,
            [matchingDispatch, unmatchedDispatch],
            capturedUtc);

        Assert.Equal(2, merged.History.Count);
        Assert.Single(merged.History, item => item.HistorySource == "native");
        var dispatchHistory = Assert.Single(merged.History, item => item.Id == "dispatch-unmatched");
        Assert.Equal("dispatch-derived", dispatchHistory.HistorySource);
        Assert.Equal("client-item-2", dispatchHistory.ExternalId);
    }

    [Fact]
    public void Queue_and_native_history_errors_are_attributed_when_an_adapter_only_supplies_status()
    {
        var queue = new DownloadQueueItem(
            Id: "queue-1",
            ClientId: "client-1",
            ClientName: "Fixture Client",
            Protocol: "qbittorrent",
            MediaType: "movies",
            Title: "Example",
            ReleaseName: "Example.2026.1080p.WEB",
            Category: "movies",
            Status: "failed",
            Progress: 0,
            SpeedMbps: 0,
            EtaSeconds: 0,
            SizeBytes: 1,
            DownloadedBytes: 0,
            Peers: 0,
            IndexerName: "Fixture Indexer",
            ErrorMessage: null,
            AddedUtc: DateTimeOffset.UtcNow);

        var normalizedQueue = DownloadClientHelpers.NormalizeQueueFailure(queue);
        Assert.NotNull(normalizedQueue.Failure);
        Assert.Equal(IntegrationFailureKind.RejectedAction, normalizedQueue.Failure!.Kind);
        Assert.Equal("queue", normalizedQueue.Failure.Operation);
        Assert.Equal("queue-1", normalizedQueue.Failure.ExternalId);

        var history = new DownloadClientHistoryItem(
            Id: "history-1",
            ClientId: "client-1",
            ClientName: "Fixture Client",
            Protocol: "qbittorrent",
            MediaType: "movies",
            Title: "Example",
            ReleaseName: "Example.2026.1080p.WEB",
            Category: "movies",
            Outcome: "failed",
            IndexerName: "Fixture Indexer",
            SizeBytes: 1,
            CompletedUtc: DateTimeOffset.UtcNow,
            ErrorMessage: null,
            ExternalId: "native-1");

        var normalizedHistory = DownloadClientHelpers.NormalizeHistoryFailure(history);
        Assert.NotNull(normalizedHistory.Failure);
        Assert.Equal("history", normalizedHistory.Failure!.Operation);
        Assert.Equal("native-1", normalizedHistory.Failure.ExternalId);
    }

    [Theory]
    [InlineData("qbittorrent", "downloading", 0.42, null, null, DownloadQueueStatuses.Downloading)]
    [InlineData("qbittorrent", "queuedDL", 0.0, null, null, DownloadQueueStatuses.Queued)]
    [InlineData("qbittorrent", "stalledDL", 0.5, null, null, DownloadQueueStatuses.Stalled)]
    [InlineData("qbittorrent", "uploading", 1.0, null, null, DownloadQueueStatuses.ImportReady)]
    [InlineData("sabnzbd", "Paused", 12.0, null, null, DownloadQueueStatuses.Queued)]
    [InlineData("sabnzbd", "Downloading", 50.0, null, null, DownloadQueueStatuses.Downloading)]
    [InlineData("sabnzbd", "Completed", 100.0, null, null, DownloadQueueStatuses.ImportReady)]
    [InlineData("nzbget", "ERROR", 33.0, null, null, DownloadQueueStatuses.Stalled)]
    [InlineData("deluge", "Seeding", 100.0, null, null, DownloadQueueStatuses.ImportReady)]
    [InlineData("utorrent", "Queued", 12.0, null, null, DownloadQueueStatuses.Queued)]
    [InlineData("transmission", "4", 0.2, null, null, DownloadQueueStatuses.Downloading)]
    [InlineData("transmission", "0", 0.0, null, null, DownloadQueueStatuses.Queued)]
    [InlineData("transmission", "4", 1.0, null, null, DownloadQueueStatuses.ImportReady)]
    [InlineData("transmission", "4", 0.5, 3, "tracker error", DownloadQueueStatuses.Stalled)]
    public void NormalizeStatus_MapsClientStatesToCanonicalQueueStatus(
        string protocol,
        string nativeStatus,
        double progress,
        int? errorCode,
        string? errorMessage,
        string expected)
    {
        Assert.True(Registry().TryGet(protocol, out var client));
        var status = client.NormalizeStatus(nativeStatus, progress, errorCode, errorMessage);

        Assert.Equal(expected, status);
    }

    /// <summary>
    /// A client that reports an error is describing the data on disk, and that
    /// outranks the progress figure. Completion used to be tested first, so a
    /// torrent sitting at 100% in an error state was called ImportReady and
    /// handed to the import pipeline as though the client were happy with it.
    ///
    /// Deluge and uTorrent both normalise through the shared text path and
    /// neither supplies an error code, so this text is the only signal there
    /// is - which is exactly why the thin coverage on those two mattered.
    /// </summary>
    [Theory]
    [InlineData("deluge", "Error")]
    [InlineData("deluge", "error")]
    [InlineData("utorrent", "Error")]
    [InlineData("utorrent", "Stalled")]
    [InlineData("deluge", "Checking failed")]
    public void A_completed_download_the_client_calls_errored_is_not_import_ready(
        string protocol,
        string nativeStatus)
    {
        Assert.True(Registry().TryGet(protocol, out var client));

        Assert.Equal(
            DownloadQueueStatuses.Stalled,
            client.NormalizeStatus(nativeStatus, 100, null, null));

        // And the same at part-progress, so the fix is about the error winning
        // rather than about the number.
        Assert.Equal(
            DownloadQueueStatuses.Stalled,
            client.NormalizeStatus(nativeStatus, 42, null, null));
    }

    /// <summary>
    /// The states that legitimately mean "ready" must keep meaning it.
    /// </summary>
    [Theory]
    [InlineData("deluge", "Seeding")]
    [InlineData("deluge", "Downloading")]
    [InlineData("utorrent", "Seeding")]
    public void A_healthy_completed_download_is_still_import_ready(string protocol, string nativeStatus)
    {
        Assert.True(Registry().TryGet(protocol, out var client));

        Assert.Equal(
            DownloadQueueStatuses.ImportReady,
            client.NormalizeStatus(nativeStatus, 100, null, null));
    }

    private static IDownloadClientRegistry Registry()
        => new DownloadClientRegistry(
        [
            new QbittorrentDownloadClient(),
            new SabnzbdDownloadClient(null!),
            new NzbGetDownloadClient(null!),
            new TransmissionDownloadClient(null!),
            new DelugeDownloadClient(null!),
            new UTorrentDownloadClient()
        ]);

    private static DownloadDispatchItem CreateDispatch(
        string id,
        string externalId,
        DateTimeOffset createdUtc)
        => new(
            Id: id,
            LibraryId: "library-1",
            MediaType: "movies",
            EntityType: "movie",
            EntityId: "movie-1",
            ReleaseName: "Example.Movie.2026.1080p.WEB",
            IndexerName: "Fixture Indexer",
            DownloadClientId: "client-1",
            DownloadClientName: "Fixture Client",
            Status: "sent",
            NotesJson: null,
            CreatedUtc: createdUtc,
            GrabStatus: "succeeded",
            GrabAttemptedUtc: createdUtc.AddMinutes(1),
            GrabResponseCode: 200,
            GrabMessage: null,
            GrabFailureCode: null,
            GrabResponseJson: null,
            DetectedUtc: createdUtc.AddMinutes(5),
            TorrentHashOrItemId: externalId,
            DownloadedBytes: 123,
            ImportStatus: "imported",
            ImportDetectedUtc: createdUtc.AddMinutes(6),
            ImportCompletedUtc: createdUtc.AddMinutes(7),
            ImportedFilePath: @"C:\Library\Movies\Example.mkv",
            ImportFailureCode: null,
            ImportFailureMessage: null,
            CircuitOpenUntilUtc: null,
            NextRetryEligibleUtc: null,
            AttemptCount: 1);
}
