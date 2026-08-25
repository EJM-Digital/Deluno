using Deluno.Integrations.Search;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Deluno.Quality.Contracts;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;

namespace Deluno.Persistence.Tests.Integrations;

public sealed class AcquisitionDecisionPipelineTests
{
    [Fact]
    public async Task PlanAsync_blocks_when_no_sources_are_linked()
    {
        var pipeline = new AcquisitionDecisionPipeline(new StubPlanner(new MediaSearchPlan(null, [], "unused")));

        var plan = await pipeline.PlanAsync(new AcquisitionDecisionRequest(
            "Dune Part Two",
            2024,
            "movies",
            null,
            "WEB 1080p",
            Sources: [],
            DownloadClients: [DownloadClient()]));

        Assert.Equal("blocked", plan.Outcome);
        Assert.False(plan.ShouldDispatch);
        Assert.Contains("No indexers", plan.SearchResult, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlanAsync_uses_same_decision_for_preview_and_automatic_paths()
    {
        var candidate = Candidate(
            status: "preferred",
            meetsCutoff: true,
            qualityDelta: 1);
        var pipeline = new AcquisitionDecisionPipeline(new StubPlanner(new MediaSearchPlan(candidate, [candidate], "best candidate")));

        var request = new AcquisitionDecisionRequest(
            "Dune Part Two",
            2024,
            "movies",
            "WEB 720p",
            "WEB 1080p",
            Sources: [Source()],
            DownloadClients: [DownloadClient()]);

        var automatic = await pipeline.PlanAsync(request);
        var preview = await pipeline.PlanAsync(request with { PreviewOnly = true });

        Assert.Equal("matched", automatic.Outcome);
        Assert.Equal(automatic.Outcome, preview.Outcome);
        Assert.Equal(Deluno.Quality.MediaPolicyCatalog.CurrentVersion, automatic.PolicyVersion);
        Assert.True(automatic.ShouldDispatch);
        Assert.False(preview.ShouldDispatch);
        Assert.NotNull(automatic.DispatchRequest);
        Assert.Equal(automatic.SearchResult, preview.SearchResult);
    }

    [Fact]
    public async Task PlanAsync_holds_candidate_that_is_not_safe_for_automatic_dispatch()
    {
        var candidate = Candidate(
            status: "eligible",
            meetsCutoff: false,
            qualityDelta: -1);
        var pipeline = new AcquisitionDecisionPipeline(new StubPlanner(new MediaSearchPlan(candidate, [candidate], "below cutoff")));

        var plan = await pipeline.PlanAsync(new AcquisitionDecisionRequest(
            "Dune Part Two",
            2024,
            "movies",
            "WEB 1080p",
            "WEB 2160p",
            Sources: [Source()],
            DownloadClients: [DownloadClient()]));

        Assert.Equal("held", plan.Outcome);
        Assert.False(plan.ShouldDispatch);
        Assert.Contains("manual review", plan.SearchResult, StringComparison.OrdinalIgnoreCase);
        Assert.Single(plan.Alternatives);
    }

    [Fact]
    public async Task PlanAsync_explains_every_candidate_without_a_twelve_item_cap()
    {
        var candidates = Enumerable.Range(1, 20)
            .Select(index => Candidate("eligible", meetsCutoff: false, qualityDelta: 0) with
            {
                ReleaseName = $"Dune.Part.Two.2024.Release-{index:00}"
            })
            .ToArray();
        var pipeline = new AcquisitionDecisionPipeline(
            new StubPlanner(new MediaSearchPlan(candidates[0], candidates, "candidates")));

        var plan = await pipeline.PlanAsync(new AcquisitionDecisionRequest(
            "Dune Part Two",
            2024,
            "movies",
            null,
            "WEB 1080p",
            Sources: [Source()],
            DownloadClients: [DownloadClient()]));

        Assert.Equal(candidates.Length, plan.Alternatives.Count);
    }

    [Fact]
    public void EvaluateSelectedRelease_requires_force_for_unsafe_manual_release()
    {
        var pipeline = new AcquisitionDecisionPipeline(new StubPlanner(new MediaSearchPlan(null, [], "")));

        var blocked = pipeline.EvaluateSelectedRelease(new AcquisitionSelectedReleaseRequest(
            "Movie.2024.CAM.sample-Group",
            null,
            "Indexer",
            "https://example.test/file.torrent",
            null,
            "WEB 1080p"));
        var forced = pipeline.EvaluateSelectedRelease(new AcquisitionSelectedReleaseRequest(
            "Movie.2024.CAM.sample-Group",
            null,
            "Indexer",
            "https://example.test/file.torrent",
            null,
            "WEB 1080p",
            ForceOverride: true,
            OverrideReason: "testing override"));

        Assert.False(blocked.CanDispatch);
        Assert.True(blocked.RequiresOverride);
        Assert.Equal(Deluno.Quality.MediaPolicyCatalog.CurrentVersion, blocked.PolicyVersion);
        Assert.True(forced.CanDispatch);
        Assert.True(forced.RequiresOverride);
    }

    [Fact]
    public async Task PlanAsync_prefers_client_with_higher_historical_success()
    {
        var candidate = Candidate(status: "preferred", meetsCutoff: true, qualityDelta: 1);
        var intelligentRouting = new StubIntelligentRoutingService(new Dictionary<string, double>
        {
            ["client-a"] = 0.35,
            ["client-b"] = 0.91
        });

        var pipeline = new AcquisitionDecisionPipeline(
            new StubPlanner(new MediaSearchPlan(candidate, [candidate], "best candidate")),
            rankingModelService: null,
            intelligentRoutingService: intelligentRouting);

        var plan = await pipeline.PlanAsync(new AcquisitionDecisionRequest(
            "Dune Part Two",
            2024,
            "movies",
            "WEB 720p",
            "WEB 1080p",
            Sources: [Source()],
            DownloadClients:
            [
                DownloadClient("client-a", 5),
                DownloadClient("client-b", 50)
            ]));

        Assert.NotNull(plan.SelectedDownloadClient);
        Assert.Equal("client-b", plan.SelectedDownloadClient!.DownloadClientId);
    }

    [Fact]
    public async Task PlanAsync_never_searches_or_dispatches_against_connections_that_have_not_passed_health_checks()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var indexer = await repository.CreateIndexerAsync(new CreateIndexerRequest(
            "Untested source", "torznab", "public", "https://fixture.invalid/api", null, 1, "2000", null, "movies", true), CancellationToken.None);
        var client = await repository.CreateDownloadClientAsync(new CreateDownloadClientRequest(
            "Untested external client", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
        var request = new AcquisitionDecisionRequest(
            "Dune Part Two", 2024, "movies", null, "WEB 1080p",
            [new LibrarySourceLinkItem("source-link", "library", indexer.Id, indexer.Name, 1, "", "", time.GetUtcNow(), time.GetUtcNow())],
            [new LibraryDownloadClientLinkItem("client-link", "library", client.Id, client.Name, 1, time.GetUtcNow(), time.GetUtcNow())]);
        var pipeline = new AcquisitionDecisionPipeline(new ThrowingPlanner(), connectionsRepository: repository);

        var sourceBlocked = await pipeline.PlanAsync(request);

        Assert.Equal("blocked", sourceBlocked.Outcome);
        Assert.False(sourceBlocked.ShouldDispatch);
        Assert.Contains("Test a source successfully", sourceBlocked.SearchResult, StringComparison.OrdinalIgnoreCase);

        await repository.UpdateIndexerHealthAsync(indexer.Id, "healthy", "Fixture source verified.", null, 1, CancellationToken.None);
        var clientBlocked = await pipeline.PlanAsync(request);

        Assert.Equal("blocked", clientBlocked.Outcome);
        Assert.False(clientBlocked.ShouldDispatch);
        Assert.Contains("Test a client successfully", clientBlocked.SearchResult, StringComparison.OrdinalIgnoreCase);
    }

    private static MediaSearchCandidate Candidate(string status, bool meetsCutoff, int qualityDelta)
        => new(
            ReleaseName: "Dune.Part.Two.2024.1080p.WEB-DL-GRP",
            IndexerId: "idx",
            IndexerName: "Indexer",
            Quality: "WEB 1080p",
            Score: 1200,
            MeetsCutoff: meetsCutoff,
            Summary: "Candidate summary.",
            DownloadUrl: "https://example.test/file.torrent",
            SizeBytes: 4_000_000_000,
            Seeders: 20,
            DecisionStatus: status,
            DecisionReasons: ["reason"],
            RiskFlags: [],
            QualityDelta: qualityDelta);

    private static LibrarySourceLinkItem Source()
        => new(
            "source-link",
            "library",
            "indexer",
            "Indexer",
            10,
            "",
            "",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private static LibraryDownloadClientLinkItem DownloadClient(string id = "client", int priority = 10)
        => new(
            "client-link",
            "library",
            id,
            "qBittorrent",
            priority,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private sealed class StubPlanner(MediaSearchPlan plan) : IMediaSearchPlanner
    {
        public Task<MediaSearchPlan> BuildPlanAsync(
            string title,
            int? year,
            string mediaType,
            string? currentQuality,
            string? targetQuality,
            IReadOnlyList<LibrarySourceLinkItem> sources,
            IReadOnlyList<CustomFormatItem>? customFormats = null,
            int? seasonNumber = null,
            int? episodeNumber = null,
            IReadOnlyList<string>? allowedQualities = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(plan);
    }

    private sealed class ThrowingPlanner : IMediaSearchPlanner
    {
        public Task<MediaSearchPlan> BuildPlanAsync(
            string title,
            int? year,
            string mediaType,
            string? currentQuality,
            string? targetQuality,
            IReadOnlyList<LibrarySourceLinkItem> sources,
            IReadOnlyList<CustomFormatItem>? customFormats = null,
            int? seasonNumber = null,
            int? episodeNumber = null,
            IReadOnlyList<string>? allowedQualities = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("An unready connection must not reach the search planner.");
    }

    private sealed class StubIntelligentRoutingService(IReadOnlyDictionary<string, double> clientRates) : IIntelligentRoutingService
    {
        public Task<IntelligentRoutingSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => Task.FromResult(new IntelligentRoutingSnapshot(
                DateTimeOffset.UtcNow,
                new IntelligentRoutingPreferences(null, 0, []),
                new Dictionary<string, double>(),
                clientRates));

        public Task<IReadOnlyList<IntelligentRoutingAnomaly>> DetectAnomaliesAsync(CancellationToken cancellationToken)
            => Task.FromResult((IReadOnlyList<IntelligentRoutingAnomaly>)[]);

        public Task<double?> GetDownloadClientSuccessRateAsync(string? downloadClientId, CancellationToken cancellationToken)
        {
            if (downloadClientId is null)
            {
                return Task.FromResult<double?>(null);
            }

            return Task.FromResult<double?>(clientRates.TryGetValue(downloadClientId, out var rate) ? rate : null);
        }
    }
}
