using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// Which downloads a client's snapshot shows, for their records to be told
/// (#554). Before this, only a client's completion webhook recorded that a
/// download had appeared, so without one it was "never shown by the client" two
/// hours after its grab: on the rig, eight downloads SABnzbd had completed and
/// Deluno had handed to Weir.
/// </summary>
public sealed class WhatTheClientShowsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-22T00:00:00Z");

    [Fact]
    public void A_download_in_the_queue_is_seen_and_not_yet_finished()
    {
        var snapshot = Snapshot(queue: [Queued("hash-1", DownloadQueueStatuses.Downloading, downloadedBytes: 512)]);

        var sighting = Assert.Single(DownloadClientTelemetryService.ShownByClient(snapshot, [Dispatch("d1", "hash-1")]));

        Assert.Equal("d1", sighting.Dispatch.Id);
        Assert.False(sighting.Finished);
        Assert.Equal(512, sighting.DownloadedBytes);
    }

    /// <summary>The rig's case: the download sat in SABnzbd's own history as Completed.</summary>
    [Fact]
    public void A_download_in_the_client_history_has_finished()
    {
        var snapshot = Snapshot(history: [History("nzo-1", DownloadQueueStatuses.Completed, "native")]);

        var sighting = Assert.Single(DownloadClientTelemetryService.ShownByClient(snapshot, [Dispatch("d1", "nzo-1")]));

        Assert.True(sighting.Finished);
    }

    [Fact]
    public void A_failure_in_the_client_history_has_finished_but_a_torrent_with_an_error_has_not()
    {
        var failedInHistory = Snapshot(history: [History("nzo-1", "failed", "native")]);
        var erroringTorrent = Snapshot(
            queue: [Queued("hash-1", DownloadQueueStatuses.Stalled)],
            history: [History("hash-1", "failed", "queue-derived")]);

        Assert.True(Assert.Single(DownloadClientTelemetryService.ShownByClient(failedInHistory, [Dispatch("d1", "nzo-1")])).Finished);
        Assert.False(Assert.Single(DownloadClientTelemetryService.ShownByClient(erroringTorrent, [Dispatch("d1", "hash-1")])).Finished);
    }

    /// <summary>
    /// #647: a failure in the client's own history is not only "finished" — it
    /// is a failure the caller has to act on, carrying the client's own reason
    /// so the caller does not have to go back to the history item for it. A
    /// torrent with an error derived from the queue is neither: it may still
    /// recover, so <c>ShownByClient</c> leaves it for the strike-based health
    /// path rather than failing it outright.
    /// </summary>
    [Fact]
    public void A_native_history_failure_is_reported_as_failed_with_its_reason()
    {
        var snapshot = Snapshot(history: [History("nzo-1", "failed", "native", errorMessage: "Unpacking failed, write error or disk is full?")]);

        var sighting = Assert.Single(DownloadClientTelemetryService.ShownByClient(snapshot, [Dispatch("d1", "nzo-1")]));

        Assert.True(sighting.Finished);
        Assert.True(sighting.Failed);
        Assert.Equal("Unpacking failed, write error or disk is full?", sighting.FailureReason);
    }

    [Fact]
    public void A_queue_derived_failure_is_not_reported_as_a_failure_to_act_on()
    {
        var snapshot = Snapshot(
            queue: [Queued("hash-1", DownloadQueueStatuses.Stalled)],
            history: [History("hash-1", "failed", "queue-derived")]);

        var sighting = Assert.Single(DownloadClientTelemetryService.ShownByClient(snapshot, [Dispatch("d1", "hash-1")]));

        Assert.False(sighting.Failed);
        Assert.Null(sighting.FailureReason);
    }

    /// <summary>
    /// An earlier attempt at the same release leaves an item with the same name
    /// in the client. Only the id the client gave this grab proves it arrived.
    /// </summary>
    [Fact]
    public void The_same_release_name_under_another_id_is_not_a_sighting()
    {
        var snapshot = Snapshot(history: [History("nzo-earlier", DownloadQueueStatuses.Completed, "native")]);

        Assert.Empty(DownloadClientTelemetryService.ShownByClient(snapshot, [Dispatch("d1", "nzo-this-grab")]));
        Assert.Empty(DownloadClientTelemetryService.ShownByClient(snapshot, [Dispatch("d2", clientItemId: null)]));
    }

    [Fact]
    public void A_download_already_recorded_is_reported_again_only_when_it_finishes()
    {
        var downloading = Snapshot(queue: [Queued("hash-1", DownloadQueueStatuses.Downloading)]);
        var finished = Snapshot(queue: [Queued("hash-1", DownloadQueueStatuses.ImportReady)]);
        var seen = Dispatch("d1", "hash-1", detectedUtc: Now);
        var done = Dispatch("d1", "hash-1", detectedUtc: Now, clientCompletedUtc: Now);

        Assert.Empty(DownloadClientTelemetryService.ShownByClient(downloading, [seen]));
        Assert.True(Assert.Single(DownloadClientTelemetryService.ShownByClient(finished, [seen])).Finished);
        Assert.Empty(DownloadClientTelemetryService.ShownByClient(finished, [done]));
    }

    private static DownloadClientTelemetrySnapshot Snapshot(
        IReadOnlyList<DownloadQueueItem>? queue = null,
        IReadOnlyList<DownloadClientHistoryItem>? history = null)
        => new(
            "client-1", "SABnzbd", "usenet", null, "healthy", null,
            new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "none"),
            new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
            queue ?? [],
            history ?? [],
            Now);

    private static DownloadQueueItem Queued(string id, string status, long downloadedBytes = 0)
        => new(id, "client-1", "SABnzbd", "usenet", "movies", "Tears of Steel", "Tears.of.Steel.2012.1080p.WEB-DL-DELUNO",
            "movies", status, 50, 0, 0, 1024, downloadedBytes, 0, "Fixture", null, Now);

    private static DownloadClientHistoryItem History(string id, string outcome, string source, string? errorMessage = null)
        => new(id, "client-1", "SABnzbd", "usenet", "movies", "Tears of Steel", "Tears.of.Steel.2012.1080p.WEB-DL-DELUNO",
            "movies", outcome, "SABnzbd", 2048, Now, errorMessage, HistorySource: source, ExternalId: id);

    private static DownloadDispatchItem Dispatch(
        string id,
        string? clientItemId,
        DateTimeOffset? detectedUtc = null,
        DateTimeOffset? clientCompletedUtc = null)
        => new(
            Id: id, LibraryId: "movies-main", MediaType: "movies", EntityType: "movie", EntityId: $"movie-{id}",
            ReleaseName: "Tears.of.Steel.2012.1080p.WEB-DL-DELUNO", IndexerName: "Fixture",
            DownloadClientId: "client-1", DownloadClientName: "SABnzbd", Status: "sent", NotesJson: null, CreatedUtc: Now,
            GrabStatus: "succeeded", GrabAttemptedUtc: Now, GrabResponseCode: 200, GrabMessage: null, GrabFailureCode: null,
            GrabResponseJson: null, DetectedUtc: detectedUtc, TorrentHashOrItemId: clientItemId, DownloadedBytes: null,
            ImportStatus: null, ImportDetectedUtc: null, ImportCompletedUtc: null, ImportedFilePath: null,
            ImportFailureCode: null, ImportFailureMessage: null, CircuitOpenUntilUtc: null,
            ClientCompletedUtc: clientCompletedUtc);
}
