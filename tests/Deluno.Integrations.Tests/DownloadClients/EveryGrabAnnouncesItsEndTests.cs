using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1467, golden VM (c10a3505a, 5 runs): a refused film's card left the board's Processing lane but stayed as "Sending to
/// qBittorrent" for minutes. The Dashboard shows a grab as being sent from DispatchGrabAttempt until DispatchGrabCompleted
/// names the same send; a grab sent with no dispatch record yet (the search paths write it after) announced its attempt as
/// "unknown" and never its end. Every grab now announces both under one key.
/// </summary>
public sealed class EveryGrabAnnouncesItsEndTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(null, false)]
    [InlineData("dispatch-1", true)]
    [InlineData("dispatch-1", false)]
    public async Task A_grab_announces_its_end_under_the_key_it_announced_its_attempt_with(string? dispatchId, bool clientTakesIt)
    {
        var now = DateTimeOffset.UnixEpoch;
        var client = new DownloadClientItem(
            "client-1", "qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, now, now, now);
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(instance => instance.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([client]);
        connections.Setup(instance => instance.ListIndexersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connections.Setup(instance => instance.ListDownloadClientPathMappingsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var implementation = new Mock<IDownloadClient>();
        implementation.Setup(instance => instance.GrabAsync(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DownloadClientItem _, DownloadClientGrabRequest request, CancellationToken _) => clientTakesIt
                ? new DownloadClientGrabResult(client.Id, request.ReleaseName, true, "sent", "Sent.")
                : new DownloadClientGrabResult(client.Id, request.ReleaseName, false, "failed", "qBittorrent refused the release."));
        var registry = new Mock<IDownloadClientRegistry>();
        var captured = implementation.Object;
        registry.Setup(instance => instance.TryGet(It.IsAny<string?>(), out captured)).Returns(true);
        var dispatches = new Mock<IDownloadDispatchesRepository>();
        dispatches.Setup(instance => instance.FindDispatchesByClientItemIdsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var attempts = new List<string>();
        var ends = new List<(string Key, bool Succeeded)>();
        var realtime = new Mock<IRealtimeEventPublisher>();
        realtime.Setup(instance => instance.PublishDispatchGrabAttemptAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string key, string _, string _, string _, CancellationToken _) => attempts.Add(key))
            .Returns(Task.CompletedTask);
        realtime.Setup(instance => instance.PublishDispatchGrabCompletedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string key, string _, string _, bool succeeded, string? _, CancellationToken _) => ends.Add((key, succeeded)))
            .Returns(Task.CompletedTask);

        var service = new DownloadClientGrabService(connections.Object, registry.Object, new PassThrough(), dispatches.Object, realtime.Object);
        await service.GrabAsync("client-1", new DownloadClientGrabRequest("Gprefusedboard.2020.1080p.WEB-DL.x264-GOLDEN", "magnet:?xt=urn:btih:4427eab075082b51fc229e93f90897d33d5b290d", "movies", "movies", "Golden feed") { DispatchId = dispatchId }, CancellationToken.None);

        var attempt = Assert.Single(attempts);
        var end = Assert.Single(ends);
        Assert.Equal(attempt, end.Key);
        Assert.Equal(clientTakesIt, end.Succeeded);
        if (dispatchId is not null) Assert.Equal(dispatchId, attempt);
    }

    [Fact]
    public async Task Two_grabs_with_no_dispatch_record_are_two_sends_not_one()
    {
        var keys = new List<string>();
        var now = DateTimeOffset.UnixEpoch;
        var client = new DownloadClientItem(
            "client-1", "qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, now, now, now);
        var connections = new Mock<IConnectionsRepository>();
        connections.Setup(instance => instance.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([client]);
        connections.Setup(instance => instance.ListIndexersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connections.Setup(instance => instance.ListDownloadClientPathMappingsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var implementation = new Mock<IDownloadClient>();
        implementation.Setup(instance => instance.GrabAsync(It.IsAny<DownloadClientItem>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DownloadClientItem _, DownloadClientGrabRequest request, CancellationToken _) => new DownloadClientGrabResult(client.Id, request.ReleaseName, true, "sent", "Sent."));
        var registry = new Mock<IDownloadClientRegistry>();
        var captured = implementation.Object;
        registry.Setup(instance => instance.TryGet(It.IsAny<string?>(), out captured)).Returns(true);
        var dispatches = new Mock<IDownloadDispatchesRepository>();
        dispatches.Setup(instance => instance.FindDispatchesByClientItemIdsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var realtime = new Mock<IRealtimeEventPublisher>();
        realtime.Setup(instance => instance.PublishDispatchGrabAttemptAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string key, string _, string _, string _, CancellationToken _) => keys.Add(key))
            .Returns(Task.CompletedTask);

        var service = new DownloadClientGrabService(connections.Object, registry.Object, new PassThrough(), dispatches.Object, realtime.Object);
        await service.GrabAsync("client-1", new DownloadClientGrabRequest("One.2020.1080p", "magnet:?xt=urn:btih:1111111111111111111111111111111111111111", "movies", "movies", "Golden feed"), CancellationToken.None);
        await service.GrabAsync("client-1", new DownloadClientGrabRequest("Two.2020.1080p", "magnet:?xt=urn:btih:2222222222222222222222222222222222222222", "movies", "movies", "Golden feed"), CancellationToken.None);

        Assert.Equal(2, keys.Distinct().Count());
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
