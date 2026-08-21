using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.Search;
using Deluno.Libraries.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Quality;
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

    private static FeedMediaSearchPlanner CreatePlanner(
        IConnectionsRepository connections,
        HttpMessageHandler? handler = null,
        ILogger<FeedMediaSearchPlanner>? logger = null)
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
            logger ?? NullLogger<FeedMediaSearchPlanner>.Instance);
    }

    private static Mock<IConnectionsRepository> CreateConnections(IndexerItem indexer)
    {
        var connections = new Mock<IConnectionsRepository>();
        connections
            .Setup(repository => repository.ListIndexersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([indexer]);
        return connections;
    }

    private static IndexerItem CreateIndexer(string id, string name)
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
            MediaScope: "movies",
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

    private sealed class FixedClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FixedFeedHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/xml")
            });
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
