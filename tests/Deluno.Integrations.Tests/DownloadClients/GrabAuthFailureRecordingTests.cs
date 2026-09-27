using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search.Trackers;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #754: a grab that cannot fetch a tracker's release because its sign-in has
/// gone bad writes to the same combined Indexer History an RSS sync's own
/// auth failure does, through the shared <see cref="IIndexerAuthFailureRecorder"/>
/// -- not a copy of that recording logic kept beside the grab code.
/// </summary>
public sealed class GrabAuthFailureRecordingTests
{
    [Fact]
    public async Task A_tracker_resolve_failure_that_looks_like_a_bad_login_is_recorded_as_an_auth_failure()
    {
        var indexer = Indexer();
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(repository => repository.ListIndexersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([indexer]);
        connections.Setup(repository => repository.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Client()]);
        connections.Setup(repository => repository.ListDownloadClientPathMappingsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var trackers = new TrackerIndexers(
            new TrackerDefinitionCatalog(overrideDirectory: null, NullLogger<TrackerDefinitionCatalog>.Instance),
            new ThrowingEngine("expired API key: unauthorized"));

        var recorder = new RecordingAuthFailureRecorder();

        var service = new DownloadClientGrabService(
            connections.Object,
            Mock.Of<IDownloadClientRegistry>(),
            new PassThrough(),
            Mock.Of<IDownloadDispatchesRepository>(),
            Mock.Of<IRealtimeEventPublisher>(),
            trackerIndexers: trackers,
            indexerAuthFailureRecorder: recorder);

        var result = await service.GrabAsync("client-1", new DownloadClientGrabRequest(
            ReleaseName: "Example.Release.WEB.1080p",
            DownloadUrl: "https://eztv.example/release/1",
            MediaType: "movies",
            Category: null,
            IndexerName: indexer.Name,
            IndexerId: indexer.Id), CancellationToken.None);

        Assert.False(result.Succeeded);
        var recorded = Assert.Single(recorder.Recorded);
        Assert.Equal(indexer.Id, recorded.IndexerId);
        Assert.Equal("grab", recorded.Action);
    }

    [Fact]
    public async Task A_tracker_resolve_failure_that_does_not_look_like_a_login_problem_is_not_recorded()
    {
        var indexer = Indexer();
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(repository => repository.ListIndexersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([indexer]);
        connections.Setup(repository => repository.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Client()]);
        connections.Setup(repository => repository.ListDownloadClientPathMappingsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var trackers = new TrackerIndexers(
            new TrackerDefinitionCatalog(overrideDirectory: null, NullLogger<TrackerDefinitionCatalog>.Instance),
            new ThrowingEngine("the tracker's page did not have a download link"));

        var recorder = new RecordingAuthFailureRecorder();

        var service = new DownloadClientGrabService(
            connections.Object,
            Mock.Of<IDownloadClientRegistry>(),
            new PassThrough(),
            Mock.Of<IDownloadDispatchesRepository>(),
            Mock.Of<IRealtimeEventPublisher>(),
            trackerIndexers: trackers,
            indexerAuthFailureRecorder: recorder);

        await service.GrabAsync("client-1", new DownloadClientGrabRequest(
            ReleaseName: "Example.Release.WEB.1080p",
            DownloadUrl: "https://eztv.example/release/1",
            MediaType: "movies",
            Category: null,
            IndexerName: indexer.Name,
            IndexerId: indexer.Id), CancellationToken.None);

        Assert.Empty(recorder.Recorded);
    }

    private static DownloadClientItem Client()
    {
        var now = DateTimeOffset.UnixEpoch;
        return new DownloadClientItem(
            "client-1", "Test Client", "qbittorrent", "localhost", 8080, null, null, null, false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, now, now, now);
    }

    private static IndexerItem Indexer()
        => new(
            Id: "eztv-1",
            Name: "EZTV",
            Protocol: "tracker",
            Privacy: "public",
            BaseUrl: "https://eztv.example/",
            ApiKey: null,
            Priority: 1,
            Categories: "2000,5000",
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
            CreatedUtc: DateTimeOffset.UnixEpoch,
            UpdatedUtc: DateTimeOffset.UnixEpoch)
        {
            DefinitionId = "eztv"
        };

    private sealed class ThrowingEngine(string message) : ITrackerEngine
    {
        public Task<TrackerSearchResult> SearchAsync(TrackerConnection connection, TrackerQuery query, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<TrackerGrab> ResolveDownloadAsync(TrackerConnection connection, string link, CancellationToken cancellationToken)
            => throw new TrackerException(message);
    }

    private sealed class RecordingAuthFailureRecorder : IIndexerAuthFailureRecorder
    {
        public List<(string? IndexerId, string IndexerName, string Action, string Message)> Recorded { get; } = [];

        public Task RecordAsync(string? indexerId, string indexerName, string action, string message, string? detailsJson, CancellationToken cancellationToken)
        {
            Recorded.Add((indexerId, indexerName, action, message));
            return Task.CompletedTask;
        }
    }

    private sealed class PassThrough : IIntegrationResiliencePolicy
    {
        public async Task<IntegrationResilienceResult<T>> ExecuteAsync<T>(
            IntegrationResilienceRequest request,
            Func<CancellationToken, Task<T>> operation,
            Func<T, IntegrationResilienceOutcome> classifyResult,
            CancellationToken cancellationToken)
            => new(await operation(cancellationToken), false, false, 1, null, null);

        public bool IsCircuitOpen(string key, out DateTimeOffset retryAfterUtc)
        {
            retryAfterUtc = default;
            return false;
        }

        public Task CloseCircuitsAsync(string serviceType, string serviceId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
