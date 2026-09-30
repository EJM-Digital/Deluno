using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1062 (and #876): "block the dead release and try another" is one step in
/// the grab service, not something each search path has to remember. A caller
/// that lets Deluno choose the release says what the release was for; a grab a
/// person chose fails with its reason shown and nothing is searched again.
/// </summary>
public sealed class TheGrabServiceBlocklistsADeadReleaseTests
{
    private const string Hash = "de4158016fbc49ebf35641903cec65a0ad0d30b5";
    private const string Release = "Arrival.2016.1080p.WEB";

    private static DownloadClientGrabRequest Request(DeadReleaseFollowUp? followUp)
        => new(Release, $"magnet:?xt=urn:btih:{Hash}", "movies", "movies", "Fixture indexer") { OnDeadRelease = followUp };

    [Fact]
    public async Task A_dead_release_is_blocklisted_by_its_hash_and_a_replacement_search_is_queued()
    {
        var world = new World(DownloadQueueStatuses.DeadRelease);

        var result = await world.GrabAsync(Request(new DeadReleaseFollowUp("movie", "movie-1", "Arrival", "library-1", "Movies")));

        Assert.Equal(DownloadQueueStatuses.DeadRelease, result.Status);
        var blocked = Assert.Single(world.Blocked);
        Assert.Equal(Release, blocked.ReleaseName);
        Assert.Equal(Hash, blocked.TorrentHashOrItemId);
        Assert.Equal("movie-1", blocked.EntityId);
        Assert.Equal("movies", blocked.MediaType);
        Assert.Equal(ImportFailurePolicy.DeadOrBrokenLink, blocked.ReasonCode);
        var job = Assert.Single(world.Jobs);
        Assert.Equal("library.search.movies", job.JobType);
        Assert.Contains("movie-1", job.PayloadJson!, StringComparison.Ordinal);
        var activity = Assert.Single(world.Activity);
        Assert.Contains("blocklisted", activity, StringComparison.Ordinal);
        Assert.Contains("searching for a replacement now", activity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_release_a_person_chose_fails_with_its_reason_and_nothing_is_blocklisted_or_searched_again()
    {
        var world = new World(DownloadQueueStatuses.DeadRelease);

        var result = await world.GrabAsync(Request(followUp: null));

        Assert.Equal(DownloadQueueStatuses.DeadRelease, result.Status);
        Assert.Contains("dead link", result.Message, StringComparison.Ordinal);
        Assert.Empty(world.Blocked);
        Assert.Empty(world.Jobs);
        Assert.Empty(world.Activity);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("unavailable")]
    [InlineData("sent")]
    public async Task Anything_that_is_not_a_dead_release_is_never_blocklisted(string status)
    {
        var world = new World(status);

        await world.GrabAsync(Request(new DeadReleaseFollowUp("movie", "movie-1", "Arrival", "library-1")));

        Assert.Empty(world.Blocked);
        Assert.Empty(world.Jobs);
    }

    [Fact]
    public async Task A_client_that_has_the_release_from_elsewhere_is_blocklisted_the_same_way()
    {
        var world = new World("duplicate-from-elsewhere");

        var result = await world.GrabAsync(Request(new DeadReleaseFollowUp("movie", "movie-1", "Arrival", "library-1")));

        Assert.Equal(DownloadQueueStatuses.DeadRelease, result.Status);
        Assert.Contains("already has this one from elsewhere", result.Message, StringComparison.Ordinal);
        Assert.Single(world.Blocked);
        Assert.Single(world.Jobs);
    }

    [Fact]
    public async Task A_season_pack_searches_only_its_own_episodes_again()
    {
        var world = new World(DownloadQueueStatuses.DeadRelease);

        await world.GrabAsync(Request(new DeadReleaseFollowUp("series", "series-1", "Show", "library-1", "TV", ["episode-1", "episode-2"])) with { MediaType = "tv" });

        Assert.Equal(2, world.Jobs.Count);
        Assert.All(world.Jobs, job => Assert.Equal("library.search.tv", job.JobType));
        Assert.Contains(world.Jobs, job => job.PayloadJson!.Contains("episode-1", StringComparison.Ordinal));
        Assert.Contains(world.Jobs, job => job.PayloadJson!.Contains("episode-2", StringComparison.Ordinal));
        Assert.Equal("tv", Assert.Single(world.Blocked).MediaType);
    }

    [Fact]
    public async Task With_no_library_the_release_is_still_blocklisted_and_says_it_will_be_avoided_next_search()
    {
        var world = new World(DownloadQueueStatuses.DeadRelease);

        await world.GrabAsync(Request(new DeadReleaseFollowUp("movie", "movie-1", "Arrival", null)));

        Assert.Single(world.Blocked);
        Assert.Empty(world.Jobs);
        Assert.Contains("will pick a different release next search", Assert.Single(world.Activity), StringComparison.Ordinal);
    }

    private sealed class World
    {
        private readonly DownloadClientGrabService service;

        public World(string clientAnswer)
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
                .ReturnsAsync((DownloadClientItem _, DownloadClientGrabRequest request, CancellationToken _) => clientAnswer switch
                {
                    "sent" => new DownloadClientGrabResult(client.Id, request.ReleaseName, true, "sent", "Sent.") { ExternalId = Hash },
                    "duplicate-from-elsewhere" => new DownloadClientGrabResult(client.Id, request.ReleaseName, true, "sent", "qBittorrent already holds this release.") { ExternalId = Hash, IsDuplicateFollow = true },
                    var status => new DownloadClientGrabResult(client.Id, request.ReleaseName, false, status, "SABnzbd did not accept the release URL. It may be a dead link or a file it could not read.")
                });
            var registry = new Mock<IDownloadClientRegistry>();
            var captured = implementation.Object;
            registry.Setup(instance => instance.TryGet(It.IsAny<string?>(), out captured)).Returns(true);

            var dispatches = new Mock<IDownloadDispatchesRepository>();
            dispatches.Setup(instance => instance.FindDispatchesByClientItemIdsAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

            var blocklist = new Mock<IBlockedReleaseRepository>();
            blocklist.Setup(instance => instance.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((BlockedRelease release, CancellationToken _) =>
                {
                    Blocked.Add(release);
                    return release;
                });
            var scheduler = new Mock<IJobScheduler>();
            scheduler.Setup(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((EnqueueJobRequest request, CancellationToken _) =>
                {
                    Jobs.Add(request);
                    return new JobQueueItem(Guid.NewGuid().ToString("n"), request.JobType, "test", "queued", request.PayloadJson, 0, now, now, null, null, null, null, null, null, null, null, null, 3, null, null);
                });
            var activity = new Mock<IActivityFeedRepository>();
            activity.Setup(instance => instance.RecordActivityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string category, string message, string? details, string? job, string? entityType, string? entityId, CancellationToken _) =>
                {
                    Activity.Add(message);
                    return new ActivityEventItem(Guid.NewGuid().ToString("n"), category, message, details, job, entityType, entityId, now);
                });

            service = new DownloadClientGrabService(
                connections.Object,
                registry.Object,
                new PassThrough(),
                dispatches.Object,
                Mock.Of<IRealtimeEventPublisher>(),
                activityFeedRepository: activity.Object,
                blockedReleases: blocklist.Object,
                jobScheduler: scheduler.Object);
        }

        public List<BlockedRelease> Blocked { get; } = [];

        public List<EnqueueJobRequest> Jobs { get; } = [];

        public List<string> Activity { get; } = [];

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientGrabRequest request)
            => service.GrabAsync("client-1", request, CancellationToken.None);
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
