using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Tests.Recorded;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1470: a call Deluno sends after the grab that qBittorrent refuses is no longer swallowed. The refusals are qBittorrent 5.2.1's
/// recorded replies (<c>category-unknown</c>: 409 "Incorrect category name"; <c>share-limits</c>: 400 for a missing parameter).
/// Not yet recorded, so not tested: a refused setLocation and a refused setAutoManagement.
/// </summary>
public sealed class TheGrabSaysWhatTheClientRefusedTests
{
    private const string Hash = "d725bf5a29bcf10e3f7d2b8ebbb221b1fe0e783c";
    private const string Release = "Example.Movie.2026.1080p";

    private static DownloadClientGrabRequest Request()
        => new(Release, "magnet:?xt=urn:btih:" + Hash + "&dn=Example", "movies", "movies", "Example Indexer") { KnownInfoHash = Hash };

    private static DownloadClientItem Client(bool shareLimits = false)
        => RecordedClientCalls.Client("qbittorrent", "deluno", "secret") with
        {
            Id = "client-1",
            Name = "qBittorrent",
            ShareRatioLimit = shareLimits ? 1.5 : null
        };

    private static QbittorrentDownloadClient Rig(bool refuseCategory, bool refuseShareLimits)
    {
        var infoCalls = 0;
        var states = new[]
        {
            "sign-in", "version", "settings", "idle", "trackers", "category-moved", "auto-management-set", "location-moved", "share-limits",
            "finished-single-file", "category-unknown"
        };
        var calls = new RecordedClientCalls.Calls("qbittorrent", states.Select(RecordedReplies.Qbittorrent).ToArray(), (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("torrents/info", StringComparison.Ordinal))
                return RecordedReplies.Json(++infoCalls == 1 ? "[]" : RecordedReplies.Qbittorrent("finished-single-file").Body("torrents/info"));
            if (path.EndsWith("torrents/add", StringComparison.Ordinal)) return RecordedReplies.Json("Ok.");
            if (path.EndsWith("setCategory", StringComparison.Ordinal))
                return refuseCategory ? RecordedReplies.Qbittorrent("category-unknown").Response("torrents/setCategory") : RecordedReplies.Qbittorrent("category-moved").Response("torrents/setCategory");
            if (path.EndsWith("setShareLimits", StringComparison.Ordinal))
                return refuseShareLimits ? RecordedReplies.Qbittorrent("share-limits").Response("torrents/setShareLimits") : RecordedReplies.Qbittorrent("category-moved").Response("torrents/setCategory");
            return null;
        });
        var handler = calls.Build();
        return new QbittorrentDownloadClient(() => handler);
    }

    [Fact]
    public async Task A_refused_category_is_handed_back_with_the_clients_own_words_and_the_grab_still_stands()
    {
        var adapter = Rig(refuseCategory: true, refuseShareLimits: false);

        var result = await adapter.GrabAsync(Client(), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Hash, result.ExternalId);
        var refusal = Assert.Single(result.FollowUpRefusals!);
        Assert.Equal("setCategory", refusal.Call);
        Assert.Equal("the movies category", refusal.What);
        Assert.True(refusal.CanStopImport);
        Assert.Equal("Incorrect category name", refusal.Reason);
    }

    [Fact]
    public async Task Refused_share_limits_are_handed_back_but_cannot_stop_the_import()
    {
        var adapter = Rig(refuseCategory: false, refuseShareLimits: true);

        var result = await adapter.GrabAsync(Client(shareLimits: true), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var refusal = Assert.Single(result.FollowUpRefusals!);
        Assert.Equal("setShareLimits", refusal.Call);
        Assert.False(refusal.CanStopImport);
        Assert.StartsWith("Missing required parameters", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Calls_the_client_took_hand_back_no_refusal()
    {
        var adapter = Rig(refuseCategory: false, refuseShareLimits: false);

        var result = await adapter.GrabAsync(Client(shareLimits: true), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Null(result.FollowUpRefusals);
        Assert.Equal(["setShareLimits", "setCategory", "setAutoManagement"], result.FollowUpCallsSent);
    }

    [Fact]
    public async Task The_grab_service_logs_it_writes_it_to_Activity_and_raises_a_problem_for_the_category()
    {
        var world = new World(Rig(refuseCategory: true, refuseShareLimits: true), shareLimits: true);

        var result = await world.GrabAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(2, world.Activity.Count);
        Assert.Contains("did not take the share limits", world.Activity[0], StringComparison.Ordinal);
        Assert.DoesNotContain("import", world.Activity[0], StringComparison.Ordinal);
        Assert.Contains($"qBittorrent did not take the movies category for {Release}: Incorrect category name", world.Activity[1], StringComparison.Ordinal);
        Assert.Contains("may not be able to import it", world.Activity[1], StringComparison.Ordinal);
        var problem = Assert.Single(world.Health);
        Assert.Equal("grab-follow-up:client-1:setCategory", problem.Id);
        Assert.Equal(HealthCheckStatuses.Problem, problem.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem.Fix));
        Assert.Contains("Incorrect category name", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_later_grab_the_client_takes_clears_the_problem()
    {
        var world = new World(Rig(refuseCategory: false, refuseShareLimits: false), shareLimits: false);
        world.Earlier = new HealthCheckResultItem(
            "grab-follow-up:client-1:setCategory", HealthCheckCategories.Grab, "qBittorrent refused the movies category",
            HealthCheckStatuses.Problem, "x", null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        await world.GrabAsync();

        Assert.Empty(world.Activity);
        var cleared = Assert.Single(world.Health);
        Assert.Equal(HealthCheckStatuses.Ok, cleared.Status);
    }

    [Fact]
    public async Task With_nothing_refused_and_no_earlier_problem_nothing_is_written()
    {
        var world = new World(Rig(refuseCategory: false, refuseShareLimits: false), shareLimits: false);

        await world.GrabAsync();

        Assert.Empty(world.Activity);
        Assert.Empty(world.Health);
    }

    private sealed class World
    {
        private readonly DownloadClientGrabService service;

        public HealthCheckResultItem? Earlier { get; set; }

        public List<string> Activity { get; } = [];

        public List<HealthCheckOutcome> Health { get; } = [];

        public World(QbittorrentDownloadClient adapter, bool shareLimits)
        {
            var client = Client(shareLimits);
            var connections = new Mock<IConnectionsRepository>();
            connections.Setup(instance => instance.ListDownloadClientsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([client]);
            connections.Setup(instance => instance.ListIndexersAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            connections.Setup(instance => instance.ListDownloadClientPathMappingsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
            var registry = new Mock<IDownloadClientRegistry>();
            IDownloadClient captured = adapter;
            registry.Setup(instance => instance.TryGet(It.IsAny<string?>(), out captured)).Returns(true);
            var dispatches = new Mock<IDownloadDispatchesRepository>();
            dispatches.Setup(instance => instance.FindDispatchesByClientItemIdsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
            var activity = new Mock<IActivityFeedRepository>();
            activity.Setup(instance => instance.RecordActivityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string category, string message, string? details, string? job, string? entityType, string? entityId, CancellationToken _) =>
                {
                    Assert.Equal("download.follow-up.refused", category);
                    Activity.Add(message);
                    return new ActivityEventItem(Guid.NewGuid().ToString("n"), category, message, details, job, entityType, entityId, DateTimeOffset.UnixEpoch);
                });
            var health = new Mock<IHealthCheckRepository>();
            health.Setup(instance => instance.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Earlier);
            health.Setup(instance => instance.UpsertResultsAsync(It.IsAny<IReadOnlyList<HealthCheckOutcome>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<HealthCheckOutcome> outcomes, CancellationToken _) =>
                {
                    Health.AddRange(outcomes);
                    return [];
                });

            service = new DownloadClientGrabService(
                connections.Object,
                registry.Object,
                new PassThrough(),
                dispatches.Object,
                Mock.Of<IRealtimeEventPublisher>(),
                healthCheckRepository: health.Object,
                activityFeedRepository: activity.Object);
        }

        public Task<DownloadClientGrabResult> GrabAsync() => service.GrabAsync("client-1", Request(), CancellationToken.None);
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
