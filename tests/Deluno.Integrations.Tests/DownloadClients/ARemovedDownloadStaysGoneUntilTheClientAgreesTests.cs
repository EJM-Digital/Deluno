using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1538: a download the client accepted the removal of leaves the held reading at once. It stays gone while the client still
/// lists it, for at most two minutes; the same release sent again (the same id) is shown the moment Deluno sends it.
/// </summary>
public sealed class ARemovedDownloadStaysGoneUntilTheClientAgreesTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan by) => current += by;
    }

    private sealed class World
    {
        public Clock Time { get; } = new(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));

        public bool ClientListsIt { get; set; } = true;

        public DownloadTelemetrySnapshotStore Store { get; }

        public World()
        {
            var service = new Mock<IDownloadClientTelemetryService>();
            service.Setup(instance => instance.CaptureAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => Capture());
            var provider = new ServiceCollection().AddSingleton(service.Object).BuildServiceProvider();
            Store = new DownloadTelemetrySnapshotStore(provider.GetRequiredService<IServiceScopeFactory>(), Time, NullLogger<DownloadTelemetrySnapshotStore>.Instance);
        }

        private DownloadTelemetryCapture Capture()
        {
            var at = Time.GetUtcNow();
            DownloadQueueItem Item(string id) => new(
                id, "client-1", "qBittorrent", "qbittorrent", "movies", id, id, "movies", DownloadQueueStatuses.Downloading, 0.4, 1, 60, 1024, 400, 0,
                "Fixture", null, at.AddHours(-1))
            {
                HealthFindings = [new DownloadHealthFinding("critical", "no-progress", "No bytes.", "Nothing arrived.", "Search again.", true, true, 1)]
            };
            var queue = new List<DownloadQueueItem> { Item("other") };
            if (ClientListsIt)
            {
                queue.Add(Item("same-hash"));
            }

            var snapshot = new DownloadClientTelemetrySnapshot(
                "client-1", "qBittorrent", "qbittorrent", null, "healthy", null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "none"), DownloadQueueSummary.Of(queue), queue, [], at);
            return new DownloadTelemetryCapture(new DownloadTelemetryOverview(DownloadQueueSummary.Of(queue), [snapshot], at), []);
        }

        public async Task<bool> ListedAsync()
        {
            await Store.RefreshAsync(TimeSpan.FromTicks(-1), CancellationToken.None);
            return Store.Current!.Overview.Clients.Single().Queue.Any(item => item.Id == "same-hash");
        }
    }

    [Fact]
    public async Task A_client_that_never_stops_listing_it_is_believed_after_two_minutes()
    {
        var world = new World();
        Assert.True(await world.ListedAsync());

        world.Store.Forget("client-1", "same-hash");
        Assert.False(await world.ListedAsync());

        world.Time.Advance(DownloadTelemetrySnapshotStore.ForgetHold - TimeSpan.FromSeconds(1));
        Assert.False(await world.ListedAsync());

        world.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(await world.ListedAsync());
    }

    [Fact]
    public async Task The_same_release_sent_again_is_shown_at_once_not_after_two_minutes()
    {
        var world = new World();
        await world.ListedAsync();
        world.Store.Forget("client-1", "same-hash");
        Assert.False(await world.ListedAsync());

        // Deluno sends the release again; the client files it under the same id.
        world.Store.Release("client-1", "same-hash");

        Assert.True(await world.ListedAsync());
    }

    [Fact]
    public async Task A_dismissed_finding_leaves_the_held_reading_for_that_download_only()
    {
        var world = new World();
        await world.ListedAsync();

        world.Store.DropFindings("client-1", "same-hash", "no-progress");

        var queue = world.Store.Current!.Overview.Clients.Single().Queue;
        Assert.Empty(queue.Single(item => item.Id == "same-hash").HealthFindings ?? []);
        Assert.Single(queue.Single(item => item.Id == "other").HealthFindings!);
    }

    [Fact]
    public async Task An_ignored_download_loses_every_finding_whichever_client_holds_it()
    {
        var world = new World();
        await world.ListedAsync();

        world.Store.DropFindings(string.Empty, "same-hash", null);

        Assert.Empty(world.Store.Current!.Overview.Clients.Single().Queue.Single(item => item.Id == "same-hash").HealthFindings ?? []);
    }

    [Fact]
    public async Task Every_grab_that_the_client_accepts_releases_what_was_removed_under_that_id()
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
            .ReturnsAsync((DownloadClientItem _, DownloadClientGrabRequest request, CancellationToken _) =>
                new DownloadClientGrabResult(client.Id, request.ReleaseName, true, "sent", "Sent.") { ExternalId = "same-hash" });
        var registry = new Mock<IDownloadClientRegistry>();
        var captured = implementation.Object;
        registry.Setup(instance => instance.TryGet(It.IsAny<string?>(), out captured)).Returns(true);
        var dispatches = new Mock<IDownloadDispatchesRepository>();
        dispatches.Setup(instance => instance.FindDispatchesByClientItemIdsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var world = new World();
        await world.ListedAsync();
        world.Store.Forget("client-1", "same-hash");
        Assert.False(await world.ListedAsync());

        var grabs = new DownloadClientGrabService(
            connections.Object, registry.Object, new PassThrough(), dispatches.Object, new Mock<IRealtimeEventPublisher>().Object, snapshotStore: world.Store);
        await grabs.GrabAsync("client-1", new DownloadClientGrabRequest("Same.Release.2020.1080p", "magnet:?xt=urn:btih:4427eab075082b51fc229e93f90897d33d5b290d", "movies", "movies", "Feed"), CancellationToken.None);

        Assert.True(await world.ListedAsync());
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
