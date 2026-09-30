using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #911 (cleanup sheet B4): once a sent release never appears, Deluno looks it
/// up by hash/NZB id and name across the client's whole queue and history, in
/// every category, and distinguishes why.
///
/// <para>Exercised against <see cref="IDownloadClient"/> directly, the same
/// boundary every real adapter (qBittorrent, Transmission, Deluge, uTorrent,
/// SABnzbd, NZBGet) implements — so these cases hold for whichever adapter
/// answers, and the adapters' own tests (duplicate/rejected history mapping,
/// #886/#910) cover the family-specific classification this service reads.</para>
/// </summary>
public sealed class GrabAppearanceLookupServiceTests
{
    private const string ClientId = "client-1";

    [Fact]
    public async Task Found_under_the_expected_category_is_reported_as_expected()
    {
        var client = ReturningSnapshot(Queue([Item("release.mkv", "hash-1", "movies")]));

        var result = await new GrabAppearanceLookupService(client.Connections, client.Registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "hash-1", "release.mkv", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.FoundAsExpected, result.Outcome);
        Assert.Equal("hash-1", result.QueueItemId);
    }

    [Fact]
    public async Task Found_under_a_different_category_says_which_one()
    {
        var client = ReturningSnapshot(Queue([Item("release.mkv", "hash-1", "sonarr-extra")]));

        var result = await new GrabAppearanceLookupService(client.Connections, client.Registry, TimeProvider.System)
            .LookupAsync(ClientId, "tv", "sonarr", "hash-1", "release.mkv", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.FoundUnderAnotherCategory, result.Outcome);
        Assert.Equal("sonarr-extra", result.FoundCategory);
        Assert.Equal("sonarr", result.ExpectedCategory);
    }

    [Fact]
    public async Task A_same_named_item_is_never_matched_when_no_hash_is_known()
    {
        var client = ReturningSnapshot(Queue([Item("Example.Release.2026", "some-other-hash", "movies")]));

        var result = await new GrabAppearanceLookupService(client.Connections, client.Registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", torrentHashOrItemId: null, "Example.Release.2026", CancellationToken.None);

        // Deluno never adopts an item by name (the owner, 30 Sep 2026): with no recorded id nothing is found.
        Assert.Equal(GrabAppearanceOutcome.NotThere, result.Outcome);
    }

    [Fact]
    public async Task A_duplicate_the_client_already_held_is_reported_as_a_duplicate()
    {
        var client = ReturningSnapshot(
            Queue([]),
            History([HistoryItem("release.nzb", "nzo-1", "tv", DownloadClientHistoryOutcomes.Duplicate, native: true)]));

        var result = await new GrabAppearanceLookupService(client.Connections, client.Registry, TimeProvider.System)
            .LookupAsync(ClientId, "tv", "tv", "nzo-1", "release.nzb", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Duplicate, result.Outcome);
    }

    [Fact]
    public async Task A_release_the_client_itself_rejected_is_reported_as_rejected()
    {
        var client = ReturningSnapshot(
            Queue([]),
            History([HistoryItem("release.nzb", "nzo-1", "tv", "failed", native: true, error: "password protected archive")]));

        var result = await new GrabAppearanceLookupService(client.Connections, client.Registry, TimeProvider.System)
            .LookupAsync(ClientId, "tv", "tv", "nzo-1", "release.nzb", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Rejected, result.Outcome);
        Assert.Equal("password protected archive", result.Detail);
    }

    /// <summary>
    /// This codebase's own "no error text, no progress" guess is not the
    /// client rejecting anything (#663) -- only a client that answered
    /// directly counts.
    /// </summary>
    [Fact]
    public async Task A_queue_derived_failed_guess_is_not_read_as_a_rejection()
    {
        var client = ReturningSnapshot(
            Queue([]),
            History([HistoryItem("release.nzb", "nzo-1", "tv", "failed", native: false)]));

        var result = await new GrabAppearanceLookupService(client.Connections, client.Registry, TimeProvider.System)
            .LookupAsync(ClientId, "tv", "tv", "nzo-1", "release.nzb", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.NotThere, result.Outcome);
    }

    [Fact]
    public async Task Nothing_anywhere_is_reported_as_not_there()
    {
        var client = ReturningSnapshot(Queue([]), History([]));

        var result = await new GrabAppearanceLookupService(client.Connections, client.Registry, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "hash-1", "release.mkv", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.NotThere, result.Outcome);
    }

    [Fact]
    public async Task A_client_that_answers_with_a_configuration_problem_is_unreachable_not_bad()
    {
        var registry = new Mock<IDownloadClientRegistry>();
        var download = new Mock<IDownloadClient>();
        download.Setup(instance => instance.GetSnapshotAsync(It.IsAny<DownloadClientItem>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadClientTelemetrySnapshot(
                ClientId, "Test Client", "qbittorrent", null, "degraded", "Download client address is missing.",
                Capabilities(), Summary(), [], [], DateTimeOffset.UtcNow)
            {
                LastFailure = IntegrationFailureFactory.FromLegacy("download-client", ClientId, "Test Client", "telemetry", "configuration", "Download client address is missing.")
            });
        var captured = download.Object;
        registry.Setup(instance => instance.TryGet(It.IsAny<string?>(), out captured)).Returns(true);

        var result = await new GrabAppearanceLookupService(Connections(), registry.Object, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "hash-1", "release.mkv", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Unreachable, result.Outcome);
    }

    [Fact]
    public async Task A_client_that_throws_is_unreachable_not_bad()
    {
        var registry = new Mock<IDownloadClientRegistry>();
        var download = new Mock<IDownloadClient>();
        download.Setup(instance => instance.GetSnapshotAsync(It.IsAny<DownloadClientItem>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));
        var captured = download.Object;
        registry.Setup(instance => instance.TryGet(It.IsAny<string?>(), out captured)).Returns(true);

        var result = await new GrabAppearanceLookupService(Connections(), registry.Object, TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "hash-1", "release.mkv", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Unreachable, result.Outcome);
    }

    [Fact]
    public async Task A_client_that_no_longer_exists_is_unreachable_not_bad()
    {
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(instance => instance.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var result = await new GrabAppearanceLookupService(connections.Object, Mock.Of<IDownloadClientRegistry>(), TimeProvider.System)
            .LookupAsync(ClientId, "movies", "movies", "hash-1", "release.mkv", CancellationToken.None);

        Assert.Equal(GrabAppearanceOutcome.Unreachable, result.Outcome);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static (IConnectionsRepository Connections, IDownloadClientRegistry Registry) ReturningSnapshot(
        IReadOnlyList<DownloadQueueItem> queue,
        IReadOnlyList<DownloadClientHistoryItem>? history = null)
    {
        var registry = new Mock<IDownloadClientRegistry>();
        var download = new Mock<IDownloadClient>();
        download.Setup(instance => instance.GetSnapshotAsync(It.IsAny<DownloadClientItem>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadClientTelemetrySnapshot(
                ClientId, "Test Client", "qbittorrent", "http://localhost:8080", "healthy", null,
                Capabilities(), Summary(), queue, history ?? [], DateTimeOffset.UtcNow));
        download.Setup(instance => instance.GetHistoryAsync(It.IsAny<DownloadClientItem>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(history ?? []);
        var captured = download.Object;
        registry.Setup(instance => instance.TryGet(It.IsAny<string?>(), out captured)).Returns(true);

        return (Connections(), registry.Object);
    }

    private static IConnectionsRepository Connections()
    {
        var repository = new Mock<IConnectionsRepository>();
        repository.Setup(instance => instance.ListDownloadClientsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Client()]);
        return repository.Object;
    }

    private static DownloadClientItem Client()
    {
        var now = DateTimeOffset.UnixEpoch;
        return new DownloadClientItem(
            ClientId, "Test Client", "qbittorrent", "localhost", 8080, null, null, null, false, null,
            "movies", "sonarr", null, 1, true, "healthy", null, null, null, now, now, now);
    }

    private static DownloadClientTelemetryCapabilities Capabilities()
        => new(SupportsQueue: true, SupportsHistory: true, SupportsPauseResume: true, SupportsRemove: true, SupportsRecheck: true, SupportsImportPath: true, AuthMode: "none");

    private static DownloadTelemetrySummary Summary() => new(0, 0, 0, 0, 0, 0, 0);

    private static IReadOnlyList<DownloadQueueItem> Queue(IReadOnlyList<DownloadQueueItem> items) => items;

    private static DownloadQueueItem Item(string releaseName, string id, string category)
        => new(id, ClientId, "Test Client", "qbittorrent", "movies", releaseName, releaseName, category,
            DownloadQueueStatuses.Downloading, 10, 1, 0, 1_000, 100, 1, "Test Indexer", null, DateTimeOffset.UtcNow);

    private static IReadOnlyList<DownloadClientHistoryItem> History(IReadOnlyList<DownloadClientHistoryItem> items) => items;

    private static DownloadClientHistoryItem HistoryItem(string releaseName, string externalId, string category, string outcome, bool native, string? error = null)
        => new(externalId, ClientId, "Test Client", native ? "sabnzbd" : "sabnzbd", category, releaseName, releaseName, category,
            outcome, "Test Indexer", 1_000, DateTimeOffset.UtcNow, error, null,
            HistorySource: native ? "native" : "queue-derived", ExternalId: externalId);
}
