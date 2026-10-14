using System.Net;
using System.Text;
using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Search;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Quality;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;

namespace Deluno.Persistence.Tests.EndToEnd;

/// <summary>
/// Reference acquisition handoff using a deterministic Torznab-compatible
/// fixture. Unlike a planner stub, this exercises the configured source URL,
/// feed parsing, release policy, ranking and generated download request.
/// Dispatch and import are exercised independently through external-client fixtures.
/// </summary>
public sealed class ReferenceSearchFlowTests
{
    [Fact]
    public async Task Configured_request_interval_paces_concurrent_searches_for_the_same_indexer_host()
    {
        using var storage = TestStorage.Create();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-20T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var platformRepository = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var connectionsRepository = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var indexer = await connectionsRepository.CreateIndexerAsync(new CreateIndexerRequest(
            "Paced Torznab fixture", "torznab", "public", "https://fixture.invalid/torznab/api", null,
            1, "2000", null, "movies", true, RequestIntervalSeconds: 10), CancellationToken.None);
        var handler = new TorznabFixtureHandler();
        var planner = new FeedMediaSearchPlanner(
            platformRepository,
            connectionsRepository,
            new SingleClientFactory(handler),
            new PassthroughResiliencePolicy(),
            new QualityModelService(storage.Factory, time),
            new DisabledRankingModelService(),
            new OutboundRequestThrottle(time, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<FeedMediaSearchPlanner>.Instance);
        var sources = (IReadOnlyList<LibrarySourceLinkItem>)
        [
            new LibrarySourceLinkItem("paced-source", "paced-library", indexer.Id, indexer.Name, 1, "", "", time.GetUtcNow(), time.GetUtcNow())
        ];

        var first = planner.BuildPlanAsync("Dune Part Two", 2024, "movies", null, "WEB 1080p", sources, cancellationToken: CancellationToken.None);
        var second = planner.BuildPlanAsync("Dune Part Two", 2024, "movies", null, "WEB 1080p", sources, cancellationToken: CancellationToken.None);

        await handler.WaitForFirstRequestAsync(CancellationToken.None);
        Assert.Equal(1, handler.RequestCount);

        // Keep time moving until both finish. This used to advance once, and
        // that raced the second search (#333): if it read the clock before the
        // advance but created its pacing timer after, the timer fell due ten
        // seconds past the advanced time and never fired. The test then awaited
        // it for ever, with no thread running, and xunit never reported the
        // assembly finished. Under a full parallel run the window was wide
        // enough to hang most runs; alone it almost never opened. The pacing
        // itself is proven above: the second request waited.
        var both = Task.WhenAll(first, second);
        var giveUp = DateTime.UtcNow.AddSeconds(30);
        while (!both.IsCompleted && DateTime.UtcNow < giveUp)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            await Task.WhenAny(both, Task.Delay(50)); // reason: yields so the paced searches run between fake-clock steps; the 30 s ceiling above ends it
        }

        Assert.True(both.IsCompleted, "Both paced searches finish once time moves on.");
        await both;
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Configured_torznab_source_returns_explainable_candidate_and_safe_dispatch_request()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var connectionsRepository = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var indexer = await connectionsRepository.CreateIndexerAsync(new CreateIndexerRequest(
            "Reference Torznab fixture", "torznab", "public", "https://fixture.invalid/torznab/api", null,
            1, "2000", null, "movies", true), CancellationToken.None);
        await connectionsRepository.UpdateIndexerHealthAsync(
            indexer.Id,
            "healthy",
            "Deterministic Torznab fixture verified.",
            null,
            1,
            CancellationToken.None);
        var client = await connectionsRepository.CreateDownloadClientAsync(new CreateDownloadClientRequest(
            "Reference qBittorrent", "qbittorrent", "localhost", 8080, null, null,
            "C:\\Downloads", "configured-movies", "configured-tv", null, 1, true), CancellationToken.None);
        await connectionsRepository.UpdateDownloadClientHealthAsync(
            client.Id,
            "healthy",
            "Deterministic qBittorrent fixture verified.",
            null,
            1,
            CancellationToken.None);
        var handler = new TorznabFixtureHandler();
        var planner = new FeedMediaSearchPlanner(
            repository,
            connectionsRepository,
            new SingleClientFactory(handler),
            new PassthroughResiliencePolicy(),
            new QualityModelService(storage.Factory, time),
            new DisabledRankingModelService(),
            new OutboundRequestThrottle(time, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<FeedMediaSearchPlanner>.Instance);
        var pipeline = new AcquisitionDecisionPipeline(planner, connectionsRepository: connectionsRepository);

        var plan = await pipeline.PlanAsync(new AcquisitionDecisionRequest(
            "Dune Part Two",
            2024,
            "movies",
            CurrentQuality: null,
            TargetQuality: "WEB 1080p",
            Sources:
            [
                new LibrarySourceLinkItem("reference-source", "reference-library", indexer.Id, indexer.Name, 1, "", "", time.GetUtcNow(), time.GetUtcNow())
            ],
            DownloadClients:
            [
                new LibraryDownloadClientLinkItem("reference-client-link", "reference-library", client.Id, client.Name, 1, time.GetUtcNow(), time.GetUtcNow(), Category: "reference-movies")
            ]), CancellationToken.None);

        Assert.Equal("matched", plan.Outcome);
        Assert.True(plan.ShouldDispatch);
        Assert.NotNull(plan.DispatchRequest);
        Assert.Equal("Dune.Part.Two.2024.WEB.1080p-PREFERRED", plan.DispatchRequest!.ReleaseName);
        Assert.Equal("reference-movies", plan.DispatchRequest.Category);
        Assert.Contains(plan.Alternatives, alternative => alternative.Status == "rejected" && alternative.Name.Contains("CAM", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("t=search", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("q=Dune%20Part%20Two%202024", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cat=2000", handler.Query, StringComparison.OrdinalIgnoreCase);

        var fallbackPlan = await pipeline.PlanAsync(new AcquisitionDecisionRequest(
            "Dune Part Two",
            2024,
            "movies",
            CurrentQuality: null,
            TargetQuality: "WEB 1080p",
            Sources:
            [
                new LibrarySourceLinkItem("reference-source", "reference-library", indexer.Id, indexer.Name, 1, "", "", time.GetUtcNow(), time.GetUtcNow())
            ],
            DownloadClients:
            [
                new LibraryDownloadClientLinkItem("reference-client-link", "reference-library", client.Id, client.Name, 1, time.GetUtcNow(), time.GetUtcNow())
            ]), CancellationToken.None);

        Assert.NotNull(fallbackPlan.DispatchRequest);
        Assert.Equal("configured-movies", fallbackPlan.DispatchRequest!.Category);
    }

    /// <summary>
    /// Seen on the rig before the soak, the first time a library was routed to
    /// both kinds of client. The client was chosen before the search, by
    /// priority alone, so a torrent went to SABnzbd. SABnzbd accepted the
    /// address, read the .torrent as an NZB every thirty seconds for five
    /// minutes and gave up, and Deluno recorded the grab as succeeded.
    /// </summary>
    [Fact]
    public async Task A_torrent_goes_to_the_torrent_client_even_when_a_usenet_client_is_first_in_line()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-19T00:00:00Z"));
        var (pipeline, connections, handler) = await CreatePipelineAsync(storage, time);
        var indexer = await HealthyIndexerAsync(connections, "Torznab fixture", "torznab");
        var usenet = await HealthyClientAsync(connections, "SABnzbd", "sabnzbd");
        var torrent = await HealthyClientAsync(connections, "qBittorrent", "qbittorrent");

        var plan = await pipeline.PlanAsync(Request(time, [indexer], [(usenet, 1), (torrent, 2)]), CancellationToken.None);

        Assert.True(plan.ShouldDispatch);
        Assert.Equal(torrent.Id, plan.SelectedDownloadClient?.DownloadClientId);
        Assert.Equal(1, handler.RequestCount);
    }

    /// <summary>
    /// Its best release would win the search and have nowhere to go, so the
    /// source is not asked, and the reason given is the real one.
    /// </summary>
    [Fact]
    public async Task A_source_no_routed_client_can_take_releases_from_is_not_searched()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-19T00:00:00Z"));
        var (pipeline, connections, handler) = await CreatePipelineAsync(storage, time);
        var indexer = await HealthyIndexerAsync(connections, "Torznab fixture", "torznab");
        var usenet = await HealthyClientAsync(connections, "SABnzbd", "sabnzbd");

        var plan = await pipeline.PlanAsync(Request(time, [indexer], [(usenet, 1)]), CancellationToken.None);

        Assert.Equal(0, handler.RequestCount);
        Assert.False(plan.ShouldDispatch);
        Assert.Contains("can take what its search sources offer", plan.SearchPlan.Summary);
    }

    [Fact]
    public async Task A_release_picked_by_hand_is_matched_to_a_client_the_same_way()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-19T00:00:00Z"));
        var (pipeline, connections, _) = await CreatePipelineAsync(storage, time);
        var torznab = await HealthyIndexerAsync(connections, "Torznab fixture", "torznab");
        var newznab = await HealthyIndexerAsync(connections, "Newznab fixture", "newznab");
        var usenet = await HealthyClientAsync(connections, "SABnzbd", "sabnzbd");
        var torrent = await HealthyClientAsync(connections, "qBittorrent", "qbittorrent");
        var both = Request(time, [torznab], [(usenet, 1), (torrent, 2)]).DownloadClients;
        var usenetOnly = Request(time, [torznab], [(usenet, 1)]).DownloadClients;

        Assert.Equal(torrent.Id, (await pipeline.ChooseDownloadClientAsync(torznab.Id, both))?.DownloadClientId);
        Assert.Equal(usenet.Id, (await pipeline.ChooseDownloadClientAsync(newznab.Id, both))?.DownloadClientId);
        Assert.Null(await pipeline.ChooseDownloadClientAsync(torznab.Id, usenetOnly));
    }

    // Golden path rc.30.9: with Deluge switched off, "Send anyway" chose Deluge anyway and the send was never made.
    [Fact]
    public async Task A_forced_send_only_considers_clients_that_are_switched_on_and_says_so_when_none_is()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-19T00:00:00Z"));
        var (pipeline, connections, _) = await CreatePipelineAsync(storage, time);
        var indexer = await HealthyIndexerAsync(connections, "Torznab fixture", "torznab");
        var deluge = await HealthyClientAsync(connections, "Deluge", "deluge");
        var qbit = await HealthyClientAsync(connections, "qBittorrent", "qbittorrent");
        var links = Request(time, [indexer], [(deluge, 1), (qbit, 2)]).DownloadClients;

        await connections.UpdateDownloadClientAsync(deluge.Id, new UpdateDownloadClientRequest(null, null, null, null, null, null, null, null, null, null, null, IsEnabled: false), CancellationToken.None);

        Assert.Equal(qbit.Id, (await pipeline.ChooseDownloadClientAsync(indexer.Id, links))?.DownloadClientId);

        await connections.UpdateDownloadClientAsync(qbit.Id, new UpdateDownloadClientRequest(null, null, null, null, null, null, null, null, null, null, null, IsEnabled: false), CancellationToken.None);

        Assert.Null(await pipeline.ChooseDownloadClientAsync(indexer.Id, links));
        var said = await pipeline.ExplainNoDownloadClientAsync(indexer.Id, links);
        Assert.Contains("switched off", said);
        Assert.Contains("Deluge", said);
    }

    /// <summary>
    /// Radarr/Sonarr's own tag rule (#808): a client carrying tags is used
    /// only for a title sharing one of them, so an anime library can keep its
    /// own client without every other title losing access to the general one.
    /// </summary>
    [Fact]
    public async Task A_tagged_client_is_used_only_for_a_title_sharing_its_tag()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-19T00:00:00Z"));
        var (pipeline, connections, _) = await CreatePipelineAsync(storage, time);
        var indexer = await HealthyIndexerAsync(connections, "Torznab fixture", "torznab");
        var general = await HealthyClientAsync(connections, "qBittorrent", "qbittorrent");
        var animeOnly = await TaggedClientAsync(connections, "qBittorrent Anime", "qbittorrent", "anime");
        // The anime client is tried first (#808): when its tag does not match, it
        // is skipped and the general client below it still takes the release.
        var links = Request(time, [indexer], [(animeOnly, 1), (general, 2)]).DownloadClients;

        Assert.Equal(general.Id, (await pipeline.ChooseDownloadClientAsync(indexer.Id, links, CancellationToken.None, titleTags: null))?.DownloadClientId);
        Assert.Equal(general.Id, (await pipeline.ChooseDownloadClientAsync(indexer.Id, links, CancellationToken.None, titleTags: ["kids"]))?.DownloadClientId);
        Assert.Equal(animeOnly.Id, (await pipeline.ChooseDownloadClientAsync(indexer.Id, links, CancellationToken.None, titleTags: ["anime"]))?.DownloadClientId);
    }

    [Fact]
    public async Task An_untagged_title_never_reaches_a_tagged_only_client_even_when_it_would_otherwise_win_on_priority()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-19T00:00:00Z"));
        var (pipeline, connections, _) = await CreatePipelineAsync(storage, time);
        var indexer = await HealthyIndexerAsync(connections, "Torznab fixture", "torznab");
        var animeOnly = await TaggedClientAsync(connections, "qBittorrent Anime", "qbittorrent", "anime");
        var links = Request(time, [indexer], [(animeOnly, 1)]).DownloadClients;

        Assert.Null(await pipeline.ChooseDownloadClientAsync(indexer.Id, links, CancellationToken.None, titleTags: null));
    }

    /// <summary>
    /// Radarr/Sonarr's DownloadClientProvider rule (#1041): a client tagged for
    /// the title wins over an untagged one even when the untagged one has the
    /// better priority; a title without the tag still goes to the untagged one.
    /// </summary>
    [Fact]
    public async Task A_client_tagged_for_the_title_wins_over_an_untagged_client_with_better_priority()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-19T00:00:00Z"));
        var (pipeline, connections, _) = await CreatePipelineAsync(storage, time);
        var indexer = await HealthyIndexerAsync(connections, "Torznab fixture", "torznab");
        var untagged = await HealthyClientAsync(connections, "qBittorrent", "qbittorrent");
        var kids = await TaggedClientAsync(connections, "qBittorrent Kids", "qbittorrent", "kids");
        var links = Request(time, [indexer], [(untagged, 1), (kids, 5)]).DownloadClients;

        Assert.Equal(kids.Id, (await pipeline.ChooseDownloadClientAsync(indexer.Id, links, CancellationToken.None, titleTags: ["kids"]))?.DownloadClientId);
        Assert.Equal(untagged.Id, (await pipeline.ChooseDownloadClientAsync(indexer.Id, links, CancellationToken.None, titleTags: null))?.DownloadClientId);
        Assert.Equal(untagged.Id, (await pipeline.ChooseDownloadClientAsync(indexer.Id, links, CancellationToken.None, titleTags: ["anime"]))?.DownloadClientId);

        var reason = await pipeline.ExplainDownloadClientRoutingAsync(kids.Id, ["kids"], CancellationToken.None);
        Assert.Contains("kids", reason);
        Assert.Contains("chosen before", reason);
    }

    private static async Task<(AcquisitionDecisionPipeline Pipeline, SqliteConnectionsRepository Connections, TorznabFixtureHandler Handler)> CreatePipelineAsync(
        TestStorage storage,
        FixedTimeProvider time)
    {
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var handler = new TorznabFixtureHandler();
        var planner = new FeedMediaSearchPlanner(
            new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage)),
            connections,
            new SingleClientFactory(handler),
            new PassthroughResiliencePolicy(),
            new QualityModelService(storage.Factory, time),
            new DisabledRankingModelService(),
            new OutboundRequestThrottle(time, NullLogger<OutboundRequestThrottle>.Instance),
            NullLogger<FeedMediaSearchPlanner>.Instance);
        return (new AcquisitionDecisionPipeline(planner, connectionsRepository: connections), connections, handler);
    }

    private static async Task<IndexerItem> HealthyIndexerAsync(SqliteConnectionsRepository connections, string name, string protocol)
    {
        var indexer = await connections.CreateIndexerAsync(new CreateIndexerRequest(
            name, protocol, "public", $"https://fixture.invalid/{protocol}/api", null,
            1, "2000", null, "movies", true), CancellationToken.None);
        await connections.UpdateIndexerHealthAsync(indexer.Id, "healthy", "Fixture verified.", null, 1, CancellationToken.None);
        return indexer;
    }

    private static async Task<DownloadClientItem> HealthyClientAsync(SqliteConnectionsRepository connections, string name, string protocol)
    {
        var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
            name, protocol, "localhost", 8080, null, null,
            @"C:\Downloads", "configured-movies", "configured-tv", null, 1, true), CancellationToken.None);
        await connections.UpdateDownloadClientHealthAsync(client.Id, "healthy", "Fixture verified.", null, 1, CancellationToken.None);
        return client;
    }

    private static async Task<DownloadClientItem> TaggedClientAsync(SqliteConnectionsRepository connections, string name, string protocol, string tags)
    {
        var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
            name, protocol, "localhost", 8080, null, null,
            @"C:\Downloads", "configured-movies", "configured-tv", null, 1, true, Tags: tags), CancellationToken.None);
        await connections.UpdateDownloadClientHealthAsync(client.Id, "healthy", "Fixture verified.", null, 1, CancellationToken.None);
        return client;
    }

    private static AcquisitionDecisionRequest Request(
        FixedTimeProvider time,
        IReadOnlyList<IndexerItem> indexers,
        IReadOnlyList<(DownloadClientItem Client, int Priority)> clients)
        => new(
            "Dune Part Two",
            2024,
            "movies",
            CurrentQuality: null,
            TargetQuality: "WEB 1080p",
            Sources: indexers
                .Select(indexer => new LibrarySourceLinkItem($"source-{indexer.Id}", "reference-library", indexer.Id, indexer.Name, 1, "", "", time.GetUtcNow(), time.GetUtcNow()))
                .ToArray(),
            DownloadClients: clients
                .Select(entry => new LibraryDownloadClientLinkItem($"link-{entry.Client.Id}", "reference-library", entry.Client.Id, entry.Client.Name, entry.Priority, time.GetUtcNow(), time.GetUtcNow()))
                .ToArray());

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class TorznabFixtureHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource firstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Query { get; private set; } = string.Empty;
        public int RequestCount { get; private set; }

        public async Task WaitForFirstRequestAsync(CancellationToken cancellationToken)
        {
            await firstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            firstRequest.TrySetResult();
            Query = request.RequestUri?.Query ?? string.Empty;
            const string feed = """
                <?xml version="1.0" encoding="UTF-8"?>
                <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
                  <channel>
                    <item>
                      <title>Dune.Part.Two.2024.CAM.720p-BAD</title>
                      <enclosure url="https://fixture.invalid/download/bad.torrent" length="1000000000" type="application/x-bittorrent" />
                      <torznab:attr name="seeders" value="10" />
                    </item>
                    <item>
                      <title>Dune.Part.Two.2024.WEB.1080p-PREFERRED</title>
                      <enclosure url="https://fixture.invalid/download/good.torrent" length="8000000000" type="application/x-bittorrent" />
                      <torznab:attr name="seeders" value="25" />
                    </item>
                  </channel>
                </rss>
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(feed, Encoding.UTF8, "application/xml")
            });
        }
    }

    private sealed class PassthroughResiliencePolicy : IIntegrationResiliencePolicy
    {
        public async Task<IntegrationResilienceResult<T>> ExecuteAsync<T>(
            IntegrationResilienceRequest request,
            Func<CancellationToken, Task<T>> operation,
            Func<T, IntegrationResilienceOutcome> classifyResult,
            CancellationToken cancellationToken)
        {
            var value = await operation(cancellationToken);
            _ = classifyResult(value);
            return new IntegrationResilienceResult<T>(value, false, false, 1, null, null);
        }

        public bool IsCircuitOpen(string key, out DateTimeOffset retryAfterUtc)
        {
            retryAfterUtc = DateTimeOffset.MinValue;
            return false;
        }

        public Task CloseCircuitsAsync(string serviceType, string serviceId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class DisabledRankingModelService : IReleaseRankingModelService
    {
        public RankingModelStatus GetStatus() => new(false, false, 0, "disabled", "Fixture uses deterministic ranking.");

        public ReleaseRankingBoostResult Score(ReleaseRankingFeatures features, bool hardBlocked)
            => new(false, false, 0, "Ranking model disabled.");
    }
}
