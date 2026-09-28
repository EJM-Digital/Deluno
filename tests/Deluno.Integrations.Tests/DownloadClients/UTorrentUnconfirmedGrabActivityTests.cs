using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Notifications;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #942: uTorrent's WebUI gives no refusal signal, only a build number, so a
/// grab that never surfaces a new hash cannot be told apart from a slow
/// answer. James's decision (28 Sep 2026): say so in plain words in Activity
/// and never blocklist on that guess -- the normal stall handling settles it
/// later.
/// </summary>
public sealed class UTorrentUnconfirmedGrabActivityTests
{
    [Fact]
    public async Task A_uTorrent_grab_that_never_surfaces_a_hash_writes_a_plain_Activity_line_and_is_not_blocklisted()
    {
        var client = Client();
        var connections = ConnectionsReturning(client);
        var registry = RegistryReturning(externalId: null);
        var activity = new Mock<IActivityFeedRepository>();
        activity.Setup(repository => repository.RecordActivityAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string category, string message, string? detailsJson, string? relatedJobId, string? relatedEntityType, string? relatedEntityId, CancellationToken _)
                => new ActivityEventItem(Guid.NewGuid().ToString("n"), category, message, detailsJson, relatedJobId, relatedEntityType, relatedEntityId, DateTimeOffset.UtcNow));

        var service = new DownloadClientGrabService(
            connections,
            registry,
            new PassThrough(),
            Mock.Of<IDownloadDispatchesRepository>(),
            Mock.Of<IRealtimeEventPublisher>(),
            activityFeedRepository: activity.Object,
            notificationService: Mock.Of<IOutboundNotificationService>());

        var result = await service.GrabAsync(client.Id, new DownloadClientGrabRequest(
            ReleaseName: "Example.Movie.2026",
            DownloadUrl: "https://indexer.example/download/123.torrent",
            MediaType: "movies",
            Category: "movies",
            IndexerName: "Example Indexer",
            DispatchId: "dispatch-1"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotEqual(DownloadQueueStatuses.DeadRelease, result.Status);
        activity.Verify(
            repository => repository.RecordActivityAsync(
                "download.grab.unconfirmed",
                It.Is<string>(message => message.Contains("never showed a new download") && message.Contains("not blocklisting")),
                It.IsAny<string?>(),
                null,
                "download",
                "dispatch-1",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_uTorrent_grab_that_surfaces_a_hash_writes_no_unconfirmed_Activity_line()
    {
        var client = Client();
        var connections = ConnectionsReturning(client);
        var registry = RegistryReturning(externalId: "abc123");
        var activity = new Mock<IActivityFeedRepository>();

        var service = new DownloadClientGrabService(
            connections,
            registry,
            new PassThrough(),
            Mock.Of<IDownloadDispatchesRepository>(),
            Mock.Of<IRealtimeEventPublisher>(),
            activityFeedRepository: activity.Object,
            notificationService: Mock.Of<IOutboundNotificationService>());

        await service.GrabAsync(client.Id, new DownloadClientGrabRequest(
            ReleaseName: "Example.Movie.2026",
            DownloadUrl: "https://indexer.example/download/123.torrent",
            MediaType: "movies",
            Category: "movies",
            IndexerName: "Example Indexer",
            DispatchId: "dispatch-1"), CancellationToken.None);

        activity.Verify(
            repository => repository.RecordActivityAsync(
                "download.grab.unconfirmed",
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_qBittorrent_grab_that_never_surfaces_a_hash_writes_no_uTorrent_specific_Activity_line()
    {
        var client = Client(protocol: "qbittorrent");
        var connections = ConnectionsReturning(client);
        var registry = RegistryReturning(externalId: null, clientId: client.Id);
        var activity = new Mock<IActivityFeedRepository>();

        var service = new DownloadClientGrabService(
            connections,
            registry,
            new PassThrough(),
            Mock.Of<IDownloadDispatchesRepository>(),
            Mock.Of<IRealtimeEventPublisher>(),
            activityFeedRepository: activity.Object,
            notificationService: Mock.Of<IOutboundNotificationService>());

        await service.GrabAsync(client.Id, new DownloadClientGrabRequest(
            ReleaseName: "Example.Movie.2026",
            DownloadUrl: "https://indexer.example/download/123.torrent",
            MediaType: "movies",
            Category: "movies",
            IndexerName: "Example Indexer",
            DispatchId: "dispatch-1"), CancellationToken.None);

        activity.Verify(
            repository => repository.RecordActivityAsync(
                "download.grab.unconfirmed",
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static DownloadClientItem Client(string protocol = "utorrent")
    {
        var now = DateTimeOffset.UnixEpoch;
        return new DownloadClientItem(
            "client-1", "Test uTorrent", protocol, "localhost", 8080, null, null, null, false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, now, now, now);
    }

    private static IConnectionsRepository ConnectionsReturning(DownloadClientItem client)
    {
        var repository = new Mock<IConnectionsRepository>();
        repository.Setup(instance => instance.ListDownloadClientsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([client]);
        repository.Setup(instance => instance.ListDownloadClientPathMappingsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return repository.Object;
    }

    private static IDownloadClientRegistry RegistryReturning(string? externalId, string clientId = "client-1")
    {
        var client = new Mock<IDownloadClient>();
        client.Setup(instance => instance.GrabAsync(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadClientGrabResult(clientId, "Example.Movie.2026", true, "sent", "Sent.") { ExternalId = externalId });

        var registry = new Mock<IDownloadClientRegistry>();
        var captured = client.Object;
        registry.Setup(instance => instance.TryGet(It.IsAny<string?>(), out captured)).Returns(true);
        return registry.Object;
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
