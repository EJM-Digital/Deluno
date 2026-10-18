using Deluno.Integrations.Search;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Deluno.Quality.Contracts;
using Deluno.Quality.ReleasePreferences;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Quality;

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
    public async Task PlanAsync_passes_a_profile_scoped_immutable_preference_plan_to_the_planner()
    {
        var candidate = Candidate(
            status: "preferred",
            meetsCutoff: true,
            qualityDelta: 1);
        var planner = new StubPlanner(new MediaSearchPlan(candidate, [candidate], "best candidate"));
        var suppliedPlan = new ReleasePreferencePlan(
            "quality-profile/pinned",
            "legacy-import/v1",
            "movies",
            []);
        var pipeline = new AcquisitionDecisionPipeline(planner);

        var result = await pipeline.PlanAsync(new AcquisitionDecisionRequest(
            "Dune Part Two",
            2024,
            "movies",
            "WEB 720p",
            "WEB 1080p",
            Sources: [Source()],
            DownloadClients: [DownloadClient()],
            PreferencePlan: suppliedPlan));

        Assert.Equal("matched", result.Outcome);
        Assert.Same(suppliedPlan, planner.ObservedPreferencePlan);
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
    public async Task Repeated_automatic_cycles_never_dispatch_an_equivalent_installed_file()
    {
        var equivalent = Candidate(
            status: "equivalent",
            meetsCutoff: true,
            qualityDelta: 0);
        var pipeline = new AcquisitionDecisionPipeline(
            new StubPlanner(new MediaSearchPlan(
                equivalent,
                [equivalent],
                "Equivalent to the installed file under the typed release plan.")));
        var request = new AcquisitionDecisionRequest(
            "Dune Part Two",
            2024,
            "movies",
            "WEB 1080p",
            "WEB 1080p",
            Sources: [Source()],
            DownloadClients: [DownloadClient()],
            SearchKind: AcquisitionSearchKinds.Automatic,
            CurrentFilePresent: true);

        var first = await pipeline.PlanAsync(request);
        var second = await pipeline.PlanAsync(request);

        Assert.Equal("held", first.Outcome);
        Assert.Equal(first.Outcome, second.Outcome);
        Assert.False(first.ShouldDispatch);
        Assert.False(second.ShouldDispatch);
        Assert.Equal("equivalent", Assert.Single(first.Alternatives).Status);
        Assert.Equal(first.SearchResult, second.SearchResult);
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
        Assert.Contains("/typed-quality/", blocked.PolicyVersion, StringComparison.Ordinal);
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

    /// <summary>
    /// #990: an indexer carrying tags is searched only for a title sharing
    /// one of them, and an untagged indexer is searched for any title — the
    /// same rule already applied to download clients (#808).
    /// </summary>
    [Fact]
    public async Task PlanAsync_searches_a_tagged_indexer_only_for_a_matching_title_and_an_untagged_indexer_for_any_title()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-28T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));

        var general = await repository.CreateIndexerAsync(new CreateIndexerRequest(
            "General indexer", "torznab", "public", "https://general.invalid/api", null, 1, "2000", null, "movies", true), CancellationToken.None);
        await repository.UpdateIndexerHealthAsync(general.Id, "healthy", "Fixture source verified.", null, 1, CancellationToken.None);

        var anime = await repository.CreateIndexerAsync(new CreateIndexerRequest(
            "Anime indexer", "torznab", "public", "https://anime.invalid/api", null, 1, "2000", "anime", "movies", true), CancellationToken.None);
        await repository.UpdateIndexerHealthAsync(anime.Id, "healthy", "Fixture source verified.", null, 1, CancellationToken.None);

        var client = await repository.CreateDownloadClientAsync(new CreateDownloadClientRequest(
            "External client", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
        await repository.UpdateDownloadClientHealthAsync(client.Id, "healthy", "Fixture client verified.", null, null, CancellationToken.None);

        var candidate = Candidate(status: "preferred", meetsCutoff: true, qualityDelta: 1);
        var pipeline = new AcquisitionDecisionPipeline(
            new StubPlanner(new MediaSearchPlan(candidate, [candidate], "best candidate")),
            connectionsRepository: repository);

        IReadOnlyList<LibrarySourceLinkItem> sources =
        [
            new LibrarySourceLinkItem("general-link", "library", general.Id, general.Name, 1, "", "", time.GetUtcNow(), time.GetUtcNow()),
            new LibrarySourceLinkItem("anime-link", "library", anime.Id, anime.Name, 2, "", "", time.GetUtcNow(), time.GetUtcNow())
        ];
        IReadOnlyList<LibraryDownloadClientLinkItem> clients =
            [new LibraryDownloadClientLinkItem("client-link", "library", client.Id, client.Name, 1, time.GetUtcNow(), time.GetUtcNow())];

        var untaggedTitle = await pipeline.PlanAsync(new AcquisitionDecisionRequest(
            "Dune Part Two", 2024, "movies", null, "WEB 1080p", sources, clients));
        Assert.Equal(1, untaggedTitle.SourceCount);

        var animeTitle = await pipeline.PlanAsync(new AcquisitionDecisionRequest(
            "Dune Part Two", 2024, "movies", null, "WEB 1080p", sources, clients, TagNames: ["anime"]));
        Assert.Equal(2, animeTitle.SourceCount);
    }

    /// <summary>
    /// #1354: a library's "Only for titles tagged" and "Never for titles tagged" choices for an indexer decide which titles that
    /// indexer is asked for, in an automatic search and in a manual (interactive) one, and a search that skips it says why.
    /// </summary>
    [Theory]
    [InlineData("automatic")]
    [InlineData("interactive")]
    public async Task PlanAsync_keeps_a_library_indexer_to_the_titles_its_tag_choices_allow_and_says_why(string searchKind)
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-28T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var indexer = await repository.CreateIndexerAsync(new CreateIndexerRequest(
            "General indexer", "torznab", "public", "https://general.invalid/api", null, 1, "2000", null, "movies", true), CancellationToken.None);
        await repository.UpdateIndexerHealthAsync(indexer.Id, "healthy", "Fixture source verified.", null, 1, CancellationToken.None);
        var client = await repository.CreateDownloadClientAsync(new CreateDownloadClientRequest(
            "External client", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
        await repository.UpdateDownloadClientHealthAsync(client.Id, "healthy", "Fixture client verified.", null, null, CancellationToken.None);
        var candidate = Candidate(status: "preferred", meetsCutoff: true, qualityDelta: 1);
        var pipeline = new AcquisitionDecisionPipeline(
            new StubPlanner(new MediaSearchPlan(candidate, [candidate], "best candidate")),
            connectionsRepository: repository);
        IReadOnlyList<LibraryDownloadClientLinkItem> clients =
            [new LibraryDownloadClientLinkItem("client-link", "library", client.Id, client.Name, 1, time.GetUtcNow(), time.GetUtcNow())];

        async Task<AcquisitionDecisionPlan> PlanFor(string only, string never, params string[] titleTags)
            => await pipeline.PlanAsync(new AcquisitionDecisionRequest(
                "Dune Part Two", 2024, "movies", null, "WEB 1080p",
                [new LibrarySourceLinkItem("link", "library", indexer.Id, indexer.Name, 1, only, never, time.GetUtcNow(), time.GetUtcNow())],
                clients, TagNames: titleTags, SearchKind: searchKind));

        Assert.Equal(1, (await PlanFor("", "")).SourceCount);
        Assert.Equal(1, (await PlanFor("kids", "", "kids", "other")).SourceCount);
        Assert.Equal(1, (await PlanFor("", "horror", "kids")).SourceCount);

        var withoutTheTag = await PlanFor("kids", "");
        Assert.Equal(0, withoutTheTag.SourceCount);
        Assert.Contains("General indexer is only used for titles tagged \"kids\"", withoutTheTag.SearchPlan.Summary);
        Assert.Contains("Dune Part Two has none of them", withoutTheTag.SearchPlan.Summary);

        var withTheTag = await PlanFor("", "horror", "horror");
        Assert.Equal(0, withTheTag.SourceCount);
        Assert.Contains("General indexer is never used for titles tagged \"horror\", and Dune Part Two has that tag.", withTheTag.SearchPlan.Summary);

        // A never-for tag wins over an only-for tag the title also holds.
        Assert.Equal(0, (await PlanFor("kids", "horror", "kids", "horror")).SourceCount);
    }

    [Fact]
    public async Task ExplainIndexerRoutingAsync_only_explains_a_tagged_indexer_a_title_actually_matched()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-28T00:00:00Z"));
        await new PlatformSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, time),
            NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var anime = await repository.CreateIndexerAsync(new CreateIndexerRequest(
            "Anime indexer", "torznab", "public", "https://anime.invalid/api", null, 1, "2000", "anime", "movies", true), CancellationToken.None);
        var pipeline = new AcquisitionDecisionPipeline(new ThrowingPlanner(), connectionsRepository: repository);

        var matched = await pipeline.ExplainIndexerRoutingAsync(anime.Id, ["kids", "anime"], CancellationToken.None);
        Assert.NotNull(matched);
        Assert.Contains("Anime indexer", matched);

        var unmatched = await pipeline.ExplainIndexerRoutingAsync(anime.Id, ["kids"], CancellationToken.None);
        Assert.Null(unmatched);
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
        public ReleasePreferencePlan? ObservedPreferencePlan { get; private set; }

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
            CancellationToken cancellationToken = default,
            IReadOnlyList<string>? tagNames = null,
            string searchKind = AcquisitionSearchKinds.Automatic,
            DateTimeOffset? availableUtc = null,
            int? currentCustomFormatScore = null,
            string? currentReleaseName = null,
            bool upgradeUntilCutoff = true,
            string? numberingScheme = null,
            int? absoluteNumber = null,
            DateOnly? airDate = null,
            int? sceneSeasonNumber = null,
            int? sceneEpisodeNumber = null,
            PreferenceEvaluationSnapshot? currentPreferenceEvaluation = null,
            ReleasePreferencePlan? preferencePlan = null,
            bool currentFilePresent = false,
            IReadOnlyList<ProfileSizeRule>? sizeRules = null,
            QualityUpgradeStopPolicy? upgradeStop = null,
            ProfileAcquisitionRules? profileAcquisition = null,
            int? runtimeMinutes = null,
            string? imdbId = null,
            ProfileScoreLimits? scoreLimits = null,
            string? genres = null,
            IReadOnlyList<string>? otherTitles = null,
            IReadOnlyList<int>? categoryIds = null,
            IReadOnlyDictionary<int, string>? episodeTitles = null,
            bool queryIsATitle = true,
            IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>>? episodeTitlesBySeason = null,
            IReadOnlyCollection<string>? patientIndexerIds = null)
        {
            ObservedPreferencePlan = preferencePlan;
            return Task.FromResult(plan);
        }
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
            CancellationToken cancellationToken = default,
            IReadOnlyList<string>? tagNames = null,
            string searchKind = AcquisitionSearchKinds.Automatic,
            DateTimeOffset? availableUtc = null,
            int? currentCustomFormatScore = null,
            string? currentReleaseName = null,
            bool upgradeUntilCutoff = true,
            string? numberingScheme = null,
            int? absoluteNumber = null,
            DateOnly? airDate = null,
            int? sceneSeasonNumber = null,
            int? sceneEpisodeNumber = null,
            PreferenceEvaluationSnapshot? currentPreferenceEvaluation = null,
            ReleasePreferencePlan? preferencePlan = null,
            bool currentFilePresent = false,
            IReadOnlyList<ProfileSizeRule>? sizeRules = null,
            QualityUpgradeStopPolicy? upgradeStop = null,
            ProfileAcquisitionRules? profileAcquisition = null,
            int? runtimeMinutes = null,
            string? imdbId = null,
            ProfileScoreLimits? scoreLimits = null,
            string? genres = null,
            IReadOnlyList<string>? otherTitles = null,
            IReadOnlyList<int>? categoryIds = null,
            IReadOnlyDictionary<int, string>? episodeTitles = null,
            bool queryIsATitle = true,
            IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>>? episodeTitlesBySeason = null,
            IReadOnlyCollection<string>? patientIndexerIds = null)
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

        public Task<DownloadClientRecord?> GetDownloadClientRecordAsync(string? downloadClientId, CancellationToken cancellationToken)
            => Task.FromResult<DownloadClientRecord?>(null);

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
