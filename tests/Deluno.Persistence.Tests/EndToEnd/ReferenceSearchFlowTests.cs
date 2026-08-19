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
            "C:\\Downloads", "movies", "tv", null, 1, true), CancellationToken.None);
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
            new DisabledRankingModelService());
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
                new LibraryDownloadClientLinkItem("reference-client-link", "reference-library", client.Id, client.Name, 1, time.GetUtcNow(), time.GetUtcNow())
            ]), CancellationToken.None);

        Assert.Equal("matched", plan.Outcome);
        Assert.True(plan.ShouldDispatch);
        Assert.NotNull(plan.DispatchRequest);
        Assert.Equal("Dune.Part.Two.2024.WEB.1080p-PREFERRED", plan.DispatchRequest!.ReleaseName);
        Assert.Contains(plan.Alternatives, alternative => alternative.Status == "rejected" && alternative.Name.Contains("CAM", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("t=search", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("q=Dune%20Part%20Two%202024", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cat=2000", handler.Query, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class TorznabFixtureHandler : HttpMessageHandler
    {
        public string Query { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
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
    }

    private sealed class DisabledRankingModelService : IReleaseRankingModelService
    {
        public RankingModelStatus GetStatus() => new(false, false, 0, "disabled", "Fixture uses deterministic ranking.");

        public ReleaseRankingBoostResult Score(ReleaseRankingFeatures features, bool hardBlocked)
            => new(false, false, 0, "Ranking model disabled.");
    }
}
