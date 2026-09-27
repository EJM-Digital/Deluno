using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #927: a download that vanishes from the client entirely - not in its
/// queue, not in its history - used to be noticed only hours later, by the
/// grab/detection timeouts ageing out. Comparing what Deluno tracks against
/// what the client actually shows, on every poll, notices it within minutes.
/// </summary>
public sealed class VanishedFromClientTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T00:00:00Z");

    [Fact]
    public void A_download_missing_from_both_queue_and_history_is_reported_as_vanished()
    {
        var snapshot = Snapshot(queue: [Queued("hash-other")]);
        var dispatch = Dispatch("d1", "hash-1", detectedUtc: Now);

        var vanished = Assert.Single(DownloadClientTelemetryService.VanishedFromClient(snapshot, [dispatch]));

        Assert.Equal("d1", vanished.Id);
    }

    [Fact]
    public void A_download_still_in_the_queue_is_not_vanished()
    {
        var snapshot = Snapshot(queue: [Queued("hash-1")]);
        var dispatch = Dispatch("d1", "hash-1", detectedUtc: Now);

        Assert.Empty(DownloadClientTelemetryService.VanishedFromClient(snapshot, [dispatch]));
    }

    [Fact]
    public void A_download_still_in_the_clients_history_is_not_vanished()
    {
        var snapshot = Snapshot(history: [History("nzo-1")]);
        var dispatch = Dispatch("d1", "nzo-1", detectedUtc: Now);

        Assert.Empty(DownloadClientTelemetryService.VanishedFromClient(snapshot, [dispatch]));
    }

    [Fact]
    public void A_download_that_never_appeared_is_the_grab_timeouts_own_story_not_this_one()
    {
        var snapshot = Snapshot(queue: [Queued("hash-other")]);
        var neverDetected = Dispatch("d1", "hash-1", detectedUtc: null);

        Assert.Empty(DownloadClientTelemetryService.VanishedFromClient(snapshot, [neverDetected]));
    }

    [Fact]
    public void An_already_imported_download_has_nothing_left_to_vanish_from()
    {
        var snapshot = Snapshot(queue: [Queued("hash-other")]);
        var imported = Dispatch("d1", "hash-1", detectedUtc: Now, importStatus: "imported");

        Assert.Empty(DownloadClientTelemetryService.VanishedFromClient(snapshot, [imported]));
    }

    [Fact]
    public void An_archived_download_is_not_reported_again()
    {
        var snapshot = Snapshot(queue: [Queued("hash-other")]);
        var archived = Dispatch("d1", "hash-1", detectedUtc: Now, status: "archived");

        Assert.Empty(DownloadClientTelemetryService.VanishedFromClient(snapshot, [archived]));
    }

    /// <summary>
    /// K3: an entirely empty queue and history from the client - a restart,
    /// or a momentary blank page - must never read as "everything vanished".
    /// </summary>
    [Fact]
    public void An_entirely_empty_response_is_not_treated_as_everything_vanishing()
    {
        var emptySnapshot = Snapshot();
        var dispatch = Dispatch("d1", "hash-1", detectedUtc: Now);

        Assert.Empty(DownloadClientTelemetryService.VanishedFromClient(emptySnapshot, [dispatch]));
    }

    private static DownloadClientTelemetrySnapshot Snapshot(
        IReadOnlyList<DownloadQueueItem>? queue = null,
        IReadOnlyList<DownloadClientHistoryItem>? history = null)
        => new(
            "client-1", "qBittorrent", "torrent", null, "healthy", null,
            new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "none"),
            new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
            queue ?? [],
            history ?? [],
            Now);

    private static DownloadQueueItem Queued(string id)
        => new(id, "client-1", "qBittorrent", "torrent", "movies", "Sintel", "Sintel.2010.1080p.WEB-DL-DELUNO",
            "movies", DownloadQueueStatuses.Downloading, 50, 0, 0, 1024, 100, 0, "Fixture", null, Now);

    private static DownloadClientHistoryItem History(string id)
        => new(id, "client-1", "qBittorrent", "torrent", "movies", "Sintel", "Sintel.2010.1080p.WEB-DL-DELUNO",
            "movies", DownloadQueueStatuses.Completed, "Fixture", 2048, Now, null, HistorySource: "native", ExternalId: id);

    private static DownloadDispatchItem Dispatch(
        string id,
        string? clientItemId,
        DateTimeOffset? detectedUtc,
        string? importStatus = null,
        string status = "sent")
        => new(
            Id: id, LibraryId: "movies-main", MediaType: "movies", EntityType: "movie", EntityId: $"movie-{id}",
            ReleaseName: "Sintel.2010.1080p.WEB-DL-DELUNO", IndexerName: "Fixture",
            DownloadClientId: "client-1", DownloadClientName: "qBittorrent", Status: status, NotesJson: null, CreatedUtc: Now,
            GrabStatus: "succeeded", GrabAttemptedUtc: Now, GrabResponseCode: 200, GrabMessage: null, GrabFailureCode: null,
            GrabResponseJson: null, DetectedUtc: detectedUtc, TorrentHashOrItemId: clientItemId, DownloadedBytes: null,
            ImportStatus: importStatus, ImportDetectedUtc: null, ImportCompletedUtc: null, ImportedFilePath: null,
            ImportFailureCode: null, ImportFailureMessage: null, CircuitOpenUntilUtc: null,
            ClientCompletedUtc: null);
}
