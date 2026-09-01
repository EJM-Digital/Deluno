using System.Net;
using System.Text;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.Search;
using Deluno.Libraries.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.Guides;
using Deluno.Quality.ReleasePreferences;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests.Search;

public sealed class FeedMediaSearchPlannerReasonTests
{
    [Fact]
    public async Task No_linked_indexers_returns_no_indexers_reason()
    {
        var connections = new Mock<IConnectionsRepository>();
        connections
            .Setup(repository => repository.ListIndexersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IndexerItem>());

        var plan = await CreatePlanner(connections.Object).BuildPlanAsync(
            "Example",
            2026,
            "movies",
            null,
            "WEB 1080p",
            [],
            cancellationToken: CancellationToken.None);

        Assert.Equal(MediaSearchReasons.NoIndexers, plan.Reason);
        Assert.Empty(plan.Candidates);
    }

    [Fact]
    public async Task Malformed_feed_returns_all_indexers_failed_and_logs_indexer_warning()
    {
        var logger = new RecordingLogger<FeedMediaSearchPlanner>();
        var indexer = CreateIndexer("malformed-indexer", "Malformed indexer");
        var connections = CreateConnections(indexer);

        var plan = await CreatePlanner(
            connections.Object,
            new FixedFeedHandler("not xml"),
            logger).BuildPlanAsync(
                "Example",
                2026,
                "movies",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                cancellationToken: CancellationToken.None);

        Assert.Equal(MediaSearchReasons.AllIndexersFailed, plan.Reason);
        Assert.Contains(logger.Messages, message =>
            message.Level == LogLevel.Warning &&
            message.Text.Contains("Malformed indexer", StringComparison.Ordinal) &&
            message.Text.Contains("could not read", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Empty_feed_returns_no_results_reason()
    {
        var indexer = CreateIndexer("empty-indexer", "Empty indexer");
        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            new FixedFeedHandler(
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <rss version="2.0"><channel /></rss>
                """),
            new RecordingLogger<FeedMediaSearchPlanner>()).BuildPlanAsync(
                "Example",
                2026,
                "movies",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                cancellationToken: CancellationToken.None);

        Assert.Equal(MediaSearchReasons.NoResults, plan.Reason);
        Assert.Empty(plan.Candidates);
    }

    [Fact]
    public async Task Feed_parser_keeps_every_returned_item_and_requests_a_protocol_limit()
    {
        var indexer = CreateIndexer("full-feed-indexer", "Full feed indexer");
        var handler = new FixedFeedHandler(CreateFeed(45));

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            handler).BuildPlanAsync(
                "Example",
                2026,
                "movies",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                cancellationToken: CancellationToken.None);

        Assert.Equal(45, plan.Candidates.Count);
        Assert.False(plan.CandidatesTruncatedByIndexer);
        Assert.Contains("limit=100", handler.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Full_indexer_page_is_reported_as_potentially_truncated()
    {
        var indexer = CreateIndexer("full-page-indexer", "Full page indexer");

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            new FixedFeedHandler(CreateFeed(100))).BuildPlanAsync(
                "Example",
                2026,
                "movies",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                cancellationToken: CancellationToken.None);

        Assert.Equal(100, plan.Candidates.Count);
        Assert.True(plan.CandidatesTruncatedByIndexer);
    }

    [Fact]
    public async Task Equivalent_release_names_are_deduplicated_after_deterministic_ranking()
    {
        var indexer = CreateIndexer("duplicate-indexer", "Duplicate indexer");
        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            new FixedFeedHandler(
                """
                <rss><channel>
                  <item><title>Example.Release.WEB.1080p</title><link>https://fixture.invalid/one</link></item>
                  <item><title>example_release_web_1080p</title><link>https://fixture.invalid/two</link></item>
                </channel></rss>
                """)).BuildPlanAsync(
                    "Example",
                    2026,
                    "movies",
                    null,
                    "WEB 1080p",
                    [CreateSource(indexer)],
                    cancellationToken: CancellationToken.None);

        Assert.Single(plan.Candidates);
        Assert.Same(plan.Candidates[0], plan.BestCandidate);
        Assert.Equal("Example.Release.WEB.1080p", plan.BestCandidate!.ReleaseName);
    }

    [Fact]
    public async Task Absolute_tv_numbering_uses_an_alternate_search_query()
    {
        var indexer = CreateIndexer("absolute-tv-indexer", "Absolute TV indexer", "tv");
        var handler = new FixedFeedHandler(CreateFeed(1));

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            handler).BuildPlanAsync(
                "Example",
                2026,
                "tv",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                seasonNumber: 1,
                episodeNumber: 2,
                numberingScheme: "absolute",
                absoluteNumber: 101,
                cancellationToken: CancellationToken.None);

        Assert.Single(plan.Candidates);
        Assert.Contains("t=search", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("q=Example%20101", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("season=", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ep=", handler.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scene_tv_numbering_uses_scene_season_and_episode_fields()
    {
        var indexer = CreateIndexer("scene-tv-indexer", "Scene TV indexer", "tv");
        var handler = new FixedFeedHandler(CreateFeed(1));

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            handler).BuildPlanAsync(
                "Example",
                2026,
                "tv",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                seasonNumber: 1,
                episodeNumber: 2,
                numberingScheme: "scene",
                sceneSeasonNumber: 3,
                sceneEpisodeNumber: 4,
                cancellationToken: CancellationToken.None);

        Assert.Single(plan.Candidates);
        Assert.Contains("t=tvsearch", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("q=Example", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("season=3", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ep=4", handler.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Season_search_keeps_the_title_clean_and_uses_the_dedicated_season_field()
    {
        var indexer = CreateIndexer("season-tv-indexer", "Season TV indexer", "tv");
        var handler = new FixedFeedHandler(CreateFeed(1));

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            handler).BuildPlanAsync(
                "Example",
                2026,
                "tv",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                seasonNumber: 2,
                cancellationToken: CancellationToken.None);

        Assert.Single(plan.Candidates);
        Assert.Contains("t=tvsearch", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("q=Example", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("season=2", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Season%202", handler.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Typed_search_exposes_and_uses_the_explicit_seeder_tie_break_family()
    {
        var indexer = CreateIndexer("seeders-indexer", "Seeders indexer");
        var handler = new FixedFeedHandler(
            """
            <rss><channel>
              <item><title>Example.Release.Z.WEB.1080p</title><link>https://fixture.invalid/z</link><torznab:attr xmlns:torznab="http://torznab.com/schemas/2015/feed" name="seeders" value="0" /></item>
              <item><title>Example.Release.A.WEB.1080p</title><link>https://fixture.invalid/a</link><torznab:attr xmlns:torznab="http://torznab.com/schemas/2015/feed" name="seeders" value="12" /></item>
            </channel></rss>
            """);

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            handler).BuildPlanAsync(
                "Example",
                2026,
                "movies",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                cancellationToken: CancellationToken.None);

        Assert.Equal(2, plan.Candidates.Count);
        Assert.Equal("Example.Release.A.WEB.1080p", plan.BestCandidate!.ReleaseName);
        var seederFamily = Assert.Single(
            plan.BestCandidate.PreferenceEvaluation!.Families,
            family => family.FamilyId == "transient.seeders");
        Assert.Equal(PreferenceFactState.Present, seederFamily.State);
        Assert.Equal("available", seederFamily.SelectedLevelId);
        Assert.DoesNotContain("score", seederFamily.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Automatic_and_interactive_typed_searches_return_the_same_installed_file_comparison()
    {
        var indexer = CreateIndexer("comparison-indexer", "Comparison indexer");
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan("movies", "WEB 2160p");
        var currentFacts = ReleasePreferenceFactFactory.FromReleaseName(
            plan,
            "Example.Release.2026.1080p.WEB-DL-GRP",
            "WEB 1080p");
        var snapshot = new PreferenceEvaluationSnapshot(
            MediaId: "movie-1",
            LibraryId: "library",
            FileIdentity: "preference-file/v1:movie-1",
            FilePath: "/library/Example.Release.2026.1080p.WEB-DL-GRP.mkv",
            FileSizeBytes: 2_000_000_000,
            PlanId: plan.Id,
            PlanVersion: plan.Version,
            PlanHash: plan.PlanHash,
            Facts: currentFacts,
            Evaluation: ReleasePreferenceEvaluator.Evaluate(plan, currentFacts),
            MatchedRuleIds: [],
            EvaluatedUtc: DateTimeOffset.UnixEpoch,
            Source: "test");
        var feed = new FixedFeedHandler(
            "<rss><channel><item><title>Example.Release.2026.2160p.WEB-DL-GRP</title><link>https://fixture.invalid/upgrade</link></item></channel></rss>");
        var planner = CreatePlanner(CreateConnections(indexer).Object, feed);

        var automatic = await planner.BuildPlanAsync(
            "Example Release",
            2026,
            "movies",
            "WEB 1080p",
            "WEB 2160p",
            [CreateSource(indexer)],
            cancellationToken: CancellationToken.None,
            searchKind: AcquisitionSearchKinds.Automatic,
            currentPreferenceEvaluation: snapshot,
            preferencePlan: plan,
            currentFilePresent: true);
        var interactive = await planner.BuildPlanAsync(
            "Example Release",
            2026,
            "movies",
            "WEB 1080p",
            "WEB 2160p",
            [CreateSource(indexer)],
            cancellationToken: CancellationToken.None,
            searchKind: AcquisitionSearchKinds.Interactive,
            currentPreferenceEvaluation: snapshot,
            preferencePlan: plan,
            currentFilePresent: true);

        var automaticCandidate = Assert.IsType<MediaSearchCandidate>(automatic.BestCandidate);
        var interactiveCandidate = Assert.IsType<MediaSearchCandidate>(interactive.BestCandidate);
        Assert.Equal(PreferenceCandidateStatus.Upgrade, automaticCandidate.PreferenceComparison?.Status);
        Assert.Equal(automaticCandidate.DecisionStatus, interactiveCandidate.DecisionStatus);
        Assert.Equal(
            JsonSerializer.Serialize(automaticCandidate.PreferenceComparison),
            JsonSerializer.Serialize(interactiveCandidate.PreferenceComparison));
        Assert.Equal(automaticCandidate.Summary, interactiveCandidate.Summary);
    }

    [Fact]
    public async Task Runtime_search_compiles_against_the_persisted_active_guide_package()
    {
        var indexer = CreateIndexer("guide-indexer", "Guide indexer");
        var guideFormat = GuidePackageCatalog.Current.CustomFormats
            .First(format => format.MappingStatus == GuideMappingStatus.Reviewed && format.MappedTraitIds.Count > 0);
        var activePackage = GuidePackageCatalog.Current with
        {
            Version = GuidePackageCatalog.Current.Version + 1,
            Source = GuidePackageCatalog.Current.Source with { UpstreamRevision = "fixture-active-revision" },
            IntegritySha256 = null
        };
        var guideStore = new Mock<IGuidePackageStore>();
        guideStore
            .Setup(store => store.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredGuidePackage(activePackage, true, DateTimeOffset.UtcNow));

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            new FixedFeedHandler(CreateFeed(1)),
            guidePackageStore: guideStore.Object).BuildPlanAsync(
                "Example",
                2026,
                "movies",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                customFormats:
                [
                    new CustomFormatItem(
                        "format-1",
                        guideFormat.Name,
                        "movies",
                        guideFormat.OriginalScore,
                        guideFormat.TrashId,
                        string.Join(";", guideFormat.Patterns),
                        true,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow)
                ],
                cancellationToken: CancellationToken.None);

        var evaluation = Assert.Single(plan.Candidates).PreferenceEvaluation;
        Assert.NotNull(evaluation);
        Assert.Equal($"{activePackage.Version}:{activePackage.Source.UpstreamRevision}", evaluation.PlanVersion);
    }

    private static FeedMediaSearchPlanner CreatePlanner(
        IConnectionsRepository connections,
        HttpMessageHandler? handler = null,
        ILogger<FeedMediaSearchPlanner>? logger = null,
        IGuidePackageStore? guidePackageStore = null)
    {
        var platform = new Mock<IPlatformSettingsRepository>();
        platform
            .Setup(repository => repository.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultSettings());

        var quality = new Mock<IQualityModelService>();
        quality
            .Setup(service => service.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityModelSnapshot(
                "test",
                [new QualityTierDefinition("WEB 1080p", 70, 1.0, 20.0, 350, 3000, 50)],
                new QualityUpgradeStopPolicy(true, true),
                DateTimeOffset.UtcNow));

        var throttle = new Mock<IOutboundRequestThrottle>();
        throttle
            .Setup(service => service.TryAcquireAsync(
                It.IsAny<string>(),
                It.IsAny<OutboundRate>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<TimeSpan?>(TimeSpan.Zero));

        return new FeedMediaSearchPlanner(
            platform.Object,
            connections,
            new FixedClientFactory(handler ?? new FixedFeedHandler("<rss><channel /></rss>")),
            new PassthroughResiliencePolicy(),
            quality.Object,
            new DisabledRankingModelService(),
            throttle.Object,
            logger ?? NullLogger<FeedMediaSearchPlanner>.Instance,
            guidePackageStore: guidePackageStore);
    }

    private static Mock<IConnectionsRepository> CreateConnections(IndexerItem indexer)
    {
        var connections = new Mock<IConnectionsRepository>();
        connections
            .Setup(repository => repository.ListIndexersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([indexer]);
        return connections;
    }

    private static IndexerItem CreateIndexer(string id, string name, string mediaScope = "movies")
        => new(
            Id: id,
            Name: name,
            Protocol: "torznab",
            Privacy: "public",
            BaseUrl: "https://fixture.invalid/torznab/api",
            ApiKey: null,
            Priority: 1,
            Categories: "2000",
            Tags: "",
            MediaScope: mediaScope,
            IsEnabled: true,
            HealthStatus: "healthy",
            LastHealthMessage: null,
            LastHealthFailureCategory: null,
            LastHealthLatencyMs: null,
            LastHealthTestUtc: null,
            ConsecutiveFailures: 0,
            RateLimitedUntilUtc: null,
            DisabledReason: null,
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow);

    private static LibrarySourceLinkItem CreateSource(IndexerItem indexer)
        => new(
            "source-link",
            "library",
            indexer.Id,
            indexer.Name,
            1,
            "",
            "",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private static PlatformSettingsSnapshot DefaultSettings()
        => new(
            AppInstanceName: "Deluno",
            MovieRootPath: null,
            SeriesRootPath: null,
            DownloadsPath: null,
            IncompleteDownloadsPath: null,
            AutoStartJobs: true,
            EnableNotifications: false,
            RenameOnImport: true,
            UseHardlinks: false,
            CleanupEmptyFolders: true,
            RemoveCompletedDownloads: false,
            UnmonitorWhenCutoffMet: false,
            MovieFolderFormat: "{Movie Title} ({Release Year})",
            SeriesFolderFormat: "{Series Title}",
            EpisodeFileFormat: "S{Season:00}E{Episode:00} - {Episode Title}",
            HostBindAddress: "127.0.0.1",
            HostPort: 5099,
            UrlBase: "",
            RequireAuthentication: false,
            UiTheme: "system",
            UiDensity: "comfortable",
            DefaultMovieView: "poster",
            DefaultShowView: "poster",
            MetadataNfoEnabled: true,
            MetadataArtworkEnabled: true,
            MetadataCertificationCountry: "AU",
            MetadataLanguage: "en",
            MetadataProviderMode: "tmdb",
            MetadataBrokerUrl: "",
            MetadataBrokerConfigured: false,
            MetadataTmdbApiKeyConfigured: false,
            MetadataOmdbApiKeyConfigured: false,
            ReleaseNeverGrabPatterns: "",
            SearchScoringMode: "weighted",
            ImportRecoveryRetentionDays: 30,
            UpdatedUtc: DateTimeOffset.UtcNow);

    private static string CreateFeed(int itemCount)
        => $"<rss><channel>{string.Join(string.Empty, Enumerable.Range(1, itemCount).Select(index =>
            $"<item><title>Example.Release.{index:00}.WEB.1080p</title><link>https://fixture.invalid/release/{index}</link></item>"))}</channel></rss>";

    private sealed class FixedClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FixedFeedHandler(string payload) : HttpMessageHandler
    {
        public string Query { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Query = request.RequestUri?.Query ?? string.Empty;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/xml")
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
        public ReleaseRankingBoostResult Score(ReleaseRankingFeatures features, bool hardBlocked)
            => new(false, false, 0, "Ranking model disabled.");

        public RankingModelStatus GetStatus()
            => new(false, false, 0, "disabled", "Test ranking model is disabled.");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogMessage> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullLogger.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(new LogMessage(logLevel, formatter(state, exception)));
    }

    private sealed record LogMessage(LogLevel Level, string Text);
}
