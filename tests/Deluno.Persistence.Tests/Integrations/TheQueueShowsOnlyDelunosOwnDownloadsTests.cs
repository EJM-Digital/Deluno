using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// Cleanup sheet row M2 (#933): the Queue lists only downloads Deluno itself
/// sent.
///
/// <para>Before this, <c>DownloadClientTelemetryService.GetOverviewAsync</c>
/// returned every row a connected client reported, Deluno's or not, unmarked.
/// A torrent added by hand, or by another tool sharing the same client, showed
/// up next to Deluno's own downloads with nothing to tell them apart - and
/// nothing stopped a person mistaking somebody else's transfer for one Deluno
/// was managing.</para>
///
/// <para><c>IsDelunosOwnDownload</c> is the one test the Queue's rows pass
/// through: matched first against the dispatch Deluno recorded when it
/// grabbed the release - the hash, NZB id or client item id the client gave
/// back. A client that has not yet handed that id back still lets Deluno
/// claim the item, but only through the dispatch record itself: the same
/// client, the same release name, recorded recently. It never falls back to
/// category or label alone any more - that was the defect #933 found: a
/// category is a convention a person can reuse by hand, so on its own it
/// proves nothing about who actually sent an item, and a hand-added download
/// sitting in Deluno's own category used to be counted as Deluno's because of
/// it.</para>
/// </summary>
public sealed class TheQueueShowsOnlyDelunosOwnDownloadsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T12:00:00Z");

    [Fact]
    public void An_item_matching_Delunos_own_dispatch_by_hash_is_Delunos()
    {
        var item = Queue(id: "abc123", category: "not-deluno");
        var dispatches = new[] { Dispatch(torrentHashOrItemId: "abc123") };

        Assert.True(DownloadClientTelemetryService.IsDelunosOwnDownload(item, dispatches, Now));
    }

    [Fact]
    public void An_item_whose_id_the_client_has_not_reported_yet_is_still_matched_by_the_recent_dispatch()
    {
        // The grab succeeded a minute ago; the client has not yet handed the
        // hash back to a poll, so the dispatch still has no
        // TorrentHashOrItemId. Deluno still claims it, by the dispatch record
        // itself: same client, same release name, recent.
        var item = Queue(id: "not-yet-known-to-deluno", category: "not-deluno");
        var dispatches = new[] { Dispatch(torrentHashOrItemId: null, createdUtc: Now.AddMinutes(-1)) };

        Assert.True(DownloadClientTelemetryService.IsDelunosOwnDownload(item, dispatches, Now));
    }

    [Fact]
    public void A_stale_dispatch_still_missing_its_id_no_longer_claims_a_same_named_item()
    {
        // Long past the window a client should have reported an id in: this
        // is not "Deluno's grab, id still pending" any more, it is a stale
        // record that must not reach out and claim an unrelated later item
        // just because the release name happens to match.
        var item = Queue(id: "someone-elses-torrent", category: "not-deluno");
        var dispatches = new[] { Dispatch(torrentHashOrItemId: null, createdUtc: Now.AddDays(-3)) };

        Assert.False(DownloadClientTelemetryService.IsDelunosOwnDownload(item, dispatches, Now));
    }

    [Fact]
    public void A_hand_added_item_sitting_in_Delunos_own_category_is_not_Delunos()
    {
        // The defect #933 fixed: nothing Deluno recorded ever mentions this
        // item, so a category matching what Deluno happens to use on this
        // client must not be enough to claim it.
        var item = Queue(id: "someone-elses-torrent", category: "deluno-movies", mediaType: "movies");

        Assert.False(DownloadClientTelemetryService.IsDelunosOwnDownload(item, [], Now));
    }

    [Fact]
    public void An_item_added_by_another_tool_or_by_hand_is_not_Delunos()
    {
        var item = Queue(id: "someone-elses-torrent", category: "manual-downloads", mediaType: "movies");
        var dispatches = new[] { Dispatch(torrentHashOrItemId: "a-different-hash") };

        Assert.False(DownloadClientTelemetryService.IsDelunosOwnDownload(item, dispatches, Now));
    }

    [Fact]
    public void A_different_release_recorded_recently_does_not_claim_this_item()
    {
        // Same client, still pending an id, but a different release: the
        // release name has to line up too, not just the timing.
        var item = Queue(id: "someone-elses-torrent", category: "not-deluno", releaseName: "Someone.Elses.Download.2026.1080p.WEB");
        var dispatches = new[] { Dispatch(torrentHashOrItemId: null, createdUtc: Now.AddMinutes(-1)) };

        Assert.False(DownloadClientTelemetryService.IsDelunosOwnDownload(item, dispatches, Now));
    }

    // ------------------------------------------------------------------ helpers

    private static DownloadQueueItem Queue(string id, string category, string mediaType = "movies", string releaseName = "Example.2026.1080p.WEB")
        => new(
            Id: id,
            ClientId: "client-1",
            ClientName: "Fixture Client",
            Protocol: "qbittorrent",
            MediaType: mediaType,
            Title: "Example",
            ReleaseName: releaseName,
            Category: category,
            Status: "downloading",
            Progress: 0.5,
            SpeedMbps: 1,
            EtaSeconds: 60,
            SizeBytes: 1000,
            DownloadedBytes: 500,
            Peers: 3,
            IndexerName: "Fixture Indexer",
            ErrorMessage: null,
            AddedUtc: DateTimeOffset.UnixEpoch);

    private static DownloadDispatchItem Dispatch(string? torrentHashOrItemId, DateTimeOffset? createdUtc = null)
        => new(
            Id: "dispatch-1",
            LibraryId: "library-1",
            MediaType: "movies",
            EntityType: "movie",
            EntityId: "movie-1",
            ReleaseName: "Example.2026.1080p.WEB",
            IndexerName: "Fixture Indexer",
            DownloadClientId: "client-1",
            DownloadClientName: "Fixture Client",
            Status: "sent",
            NotesJson: null,
            CreatedUtc: createdUtc ?? DateTimeOffset.UnixEpoch,
            GrabStatus: "succeeded",
            GrabAttemptedUtc: createdUtc ?? DateTimeOffset.UnixEpoch,
            GrabResponseCode: 200,
            GrabMessage: null,
            GrabFailureCode: null,
            GrabResponseJson: null,
            DetectedUtc: null,
            TorrentHashOrItemId: torrentHashOrItemId,
            DownloadedBytes: null,
            ImportStatus: null,
            ImportDetectedUtc: null,
            ImportCompletedUtc: null,
            ImportedFilePath: null,
            ImportFailureCode: null,
            ImportFailureMessage: null,
            CircuitOpenUntilUtc: null,
            NextRetryEligibleUtc: null,
            AttemptCount: 1);
}
