using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Notifications;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// Every grab confirms the client actually saved the release where the
/// requesting library expects it (#909). A category renamed in the client or
/// a remapped share used to "succeed" here while the file landed somewhere
/// nothing was watching, and the first sign of trouble was an import that
/// never happened. This is flagged immediately, as a setup problem -- not
/// judged as a bad release.
/// </summary>
public sealed class PerGrabSavePathTests
{
    [Fact]
    public async Task A_grab_whose_reported_save_path_disagrees_with_the_library_is_flagged()
    {
        var client = Client();
        var library = Library(downloadsPath: "/data/downloads/movies");
        var connections = ConnectionsReturning(client);
        var libraries = LibrariesReturning(library, category: "movies", clientId: client.Id);
        var registry = RegistryReturning(reportedSavePath: "/data/downloads/wrong-folder");
        var healthChecks = new Mock<IHealthCheckRepository>();
        healthChecks.Setup(repository => repository.UpsertResultsAsync(It.IsAny<IReadOnlyList<HealthCheckOutcome>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<HealthCheckOutcome> outcomes, CancellationToken _) => outcomes
                .Select(outcome => new HealthCheckTransitionResult(
                    new HealthCheckResultItem(outcome.Id, outcome.Category, outcome.Name, outcome.Status, outcome.Message, outcome.Cause, outcome.Fix, outcome.DetailsJson, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null),
                    HealthCheckTransition.Started))
                .ToArray());

        var service = new DownloadClientGrabService(
            connections,
            registry,
            new PassThrough(),
            Mock.Of<IDownloadDispatchesRepository>(),
            Mock.Of<IRealtimeEventPublisher>(),
            trackerIndexers: null,
            librariesRepository: libraries,
            healthCheckRepository: healthChecks.Object,
            activityFeedRepository: Mock.Of<IActivityFeedRepository>(),
            notificationService: Mock.Of<IOutboundNotificationService>());

        await service.GrabAsync(client.Id, new DownloadClientGrabRequest(
            ReleaseName: "Example.Movie.2026",
            DownloadUrl: "magnet:?xt=urn:btih:abc",
            MediaType: "movies",
            Category: "movies",
            IndexerName: null), CancellationToken.None);

        healthChecks.Verify(
            repository => repository.UpsertResultsAsync(
                It.Is<IReadOnlyList<HealthCheckOutcome>>(outcomes =>
                    outcomes.Count == 1 &&
                    outcomes[0].Status == HealthCheckStatuses.Problem &&
                    outcomes[0].Category == HealthCheckCategories.Grab),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_grab_whose_save_path_matches_is_not_flagged()
    {
        var client = Client();
        var library = Library(downloadsPath: "/data/downloads/movies");
        var connections = ConnectionsReturning(client);
        var libraries = LibrariesReturning(library, category: "movies", clientId: client.Id);
        var registry = RegistryReturning(reportedSavePath: "/data/downloads/movies");
        var healthChecks = new Mock<IHealthCheckRepository>();
        healthChecks.Setup(repository => repository.UpsertResultsAsync(It.IsAny<IReadOnlyList<HealthCheckOutcome>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<HealthCheckOutcome> outcomes, CancellationToken _) => outcomes
                .Select(outcome => new HealthCheckTransitionResult(
                    new HealthCheckResultItem(outcome.Id, outcome.Category, outcome.Name, outcome.Status, outcome.Message, outcome.Cause, outcome.Fix, outcome.DetailsJson, DateTimeOffset.UtcNow, null, null),
                    HealthCheckTransition.Unchanged))
                .ToArray());

        var service = new DownloadClientGrabService(
            connections,
            registry,
            new PassThrough(),
            Mock.Of<IDownloadDispatchesRepository>(),
            Mock.Of<IRealtimeEventPublisher>(),
            trackerIndexers: null,
            librariesRepository: libraries,
            healthCheckRepository: healthChecks.Object,
            activityFeedRepository: Mock.Of<IActivityFeedRepository>(),
            notificationService: Mock.Of<IOutboundNotificationService>());

        await service.GrabAsync(client.Id, new DownloadClientGrabRequest(
            ReleaseName: "Example.Movie.2026",
            DownloadUrl: "magnet:?xt=urn:btih:abc",
            MediaType: "movies",
            Category: "movies",
            IndexerName: null), CancellationToken.None);

        healthChecks.Verify(
            repository => repository.UpsertResultsAsync(
                It.Is<IReadOnlyList<HealthCheckOutcome>>(outcomes => outcomes.Count == 1 && outcomes[0].Status == HealthCheckStatuses.Ok),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static DownloadClientItem Client()
    {
        var now = DateTimeOffset.UnixEpoch;
        return new DownloadClientItem(
            "client-1", "Test Client", "qbittorrent", "localhost", 8080, null, null, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, now, now, now);
    }

    private static LibraryItem Library(string downloadsPath)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new LibraryItem(
            "library-1", "Movies", "movies", "main", "/data/library/movies", downloadsPath, null, null, null,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
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

    private static ILibrariesRepository LibrariesReturning(LibraryItem library, string category, string clientId)
    {
        var now = DateTimeOffset.UnixEpoch;
        var repository = new Mock<ILibrariesRepository>();
        repository.Setup(instance => instance.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([library]);
        repository.Setup(instance => instance.GetLibraryRoutingAsync(library.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LibraryRoutingSnapshot(
                library.Id, library.Name, [],
                [new LibraryDownloadClientLinkItem("link-1", library.Id, clientId, "Test Client", 1, now, now, category)]));
        return repository.Object;
    }

    private static IDownloadClientRegistry RegistryReturning(string reportedSavePath)
    {
        var client = new Mock<IDownloadClient>();
        client.Setup(instance => instance.GrabAsync(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadClientGrabResult("client-1", "Example.Movie.2026", true, "sent", "Sent.") { ExternalId = "abc123" });
        client.Setup(instance => instance.CheckCategoryAsync(It.IsAny<DownloadClientItem>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadClientCategoryCheckResult("client-1", "Test Client", "movies", DownloadClientCategoryStatuses.Ready, "Ready.", true, true, reportedSavePath));

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
