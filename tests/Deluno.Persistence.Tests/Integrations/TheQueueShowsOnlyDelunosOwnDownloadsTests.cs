using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Connections.Contracts;

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
/// grabbed the release (the hash or item id the client gave back), and only
/// failing that, against the category/label Deluno routes its own downloads
/// through - the same test <c>RunConfiguredHealthRemediationAsync</c> already
/// applied before touching a client entry (<c>IsDelunoCategory</c>). Neither
/// matching an item added by another tool or by hand, so it is left off.</para>
/// </summary>
public sealed class TheQueueShowsOnlyDelunosOwnDownloadsTests
{
    [Fact]
    public void An_item_matching_Delunos_own_dispatch_by_hash_is_Delunos()
    {
        var client = Client();
        var item = Queue(id: "abc123", category: "not-deluno");
        var dispatches = new[] { Dispatch(torrentHashOrItemId: "abc123") };

        Assert.True(DownloadClientTelemetryService.IsDelunosOwnDownload(
            client, item, dispatches, RouteCategories()));
    }

    [Fact]
    public void An_item_matching_no_dispatch_but_the_configured_movies_category_is_Delunos()
    {
        var client = Client(moviesCategory: "deluno-movies");
        var item = Queue(id: "abc123", category: "deluno-movies", mediaType: "movies");

        Assert.True(DownloadClientTelemetryService.IsDelunosOwnDownload(
            client, item, [], RouteCategories()));
    }

    [Fact]
    public void An_item_added_by_another_tool_or_by_hand_is_not_Delunos()
    {
        var client = Client(moviesCategory: "deluno-movies", tvCategory: "deluno-tv");
        var item = Queue(id: "someone-elses-torrent", category: "manual-downloads", mediaType: "movies");
        var dispatches = new[] { Dispatch(torrentHashOrItemId: "a-different-hash") };

        Assert.False(DownloadClientTelemetryService.IsDelunosOwnDownload(
            client, item, dispatches, RouteCategories()));
    }

    [Fact]
    public void An_item_in_the_movies_category_is_not_claimed_by_a_tv_search()
    {
        // The category has to line up with what the item actually is, not
        // just with any category Deluno happens to use on this client -
        // otherwise a tv release routed through the movies category (or vice
        // versa) would be claimed by accident.
        var client = Client(moviesCategory: "deluno-movies", tvCategory: "deluno-tv");
        var item = Queue(id: "someone-elses-torrent", category: "deluno-tv", mediaType: "movies");

        Assert.False(DownloadClientTelemetryService.IsDelunosOwnDownload(
            client, item, [], RouteCategories()));
    }

    // ------------------------------------------------------------------ helpers

    private static DownloadClientItem Client(string? moviesCategory = null, string? tvCategory = null)
        => new(
            Id: "client-1",
            Name: "Fixture Client",
            Protocol: "qbittorrent",
            Host: "localhost",
            Port: 8080,
            Username: null,
            Secret: null,
            EndpointUrl: "http://localhost:8080",
            UseSsl: false,
            UrlBase: null,
            MoviesCategory: moviesCategory,
            TvCategory: tvCategory,
            CategoryTemplate: null,
            Priority: 0,
            IsEnabled: true,
            HealthStatus: "healthy",
            LastHealthMessage: null,
            LastHealthFailureCategory: null,
            LastHealthLatencyMs: null,
            LastHealthTestUtc: null,
            CreatedUtc: DateTimeOffset.UnixEpoch,
            UpdatedUtc: DateTimeOffset.UnixEpoch);

    private static DownloadQueueItem Queue(string id, string category, string mediaType = "movies")
        => new(
            Id: id,
            ClientId: "client-1",
            ClientName: "Fixture Client",
            Protocol: "qbittorrent",
            MediaType: mediaType,
            Title: "Example",
            ReleaseName: "Example.2026.1080p.WEB",
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

    private static DownloadDispatchItem Dispatch(string torrentHashOrItemId)
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
            CreatedUtc: DateTimeOffset.UnixEpoch,
            GrabStatus: "succeeded",
            GrabAttemptedUtc: DateTimeOffset.UnixEpoch,
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

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> RouteCategories()
        => new Dictionary<string, IReadOnlyDictionary<string, string>>();
}
