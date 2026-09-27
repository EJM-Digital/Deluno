using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// Cleanup sheet J1/J2 (#924): removing a download from the Queue asks why,
/// and each of the three plain-word choices does exactly what it says and
/// nothing else.
///
/// <para>Covers both a torrent-shaped client (qBittorrent, which refuses a
/// held copy by infohash — <see cref="DownloadClientActions.DeleteWithData"/>
/// alone is enough to let it back in) and a Usenet-shaped one (SABnzbd, which
/// remembers a refused release in history rather than the queue), because the
/// three reasons ask the client to do different things and both protocol
/// shapes have to come out the same in Deluno's own records regardless.</para>
/// </summary>
public sealed class ARemovalReasonDoesExactlyWhatItSaysTests
{
    private const string Release = "Some.Movie.2024.1080p.WEB-DL-GROUP";
    private const string Indexer = "TestIndexer";
    private const string QueueItemId = "queue-item-1";

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("sabnzbd")]
    public async Task Its_bad_removes_deletes_blocklists_everywhere_and_searches_again(string protocol)
    {
        using var world = await World.CreateAsync(protocol);

        var result = await world.RemoveAsync(QueueRemovalReasons.Failed);

        Assert.True(result.Succeeded);
        Assert.True(result.Blocklisted);
        Assert.True(result.SearchStarted);
        Assert.Equal(DownloadClientActions.DeleteWithData, world.Client.LastAction);
        Assert.Equal(QueueItemId, world.Client.LastQueueItemId);

        var refused = Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Equal(ImportFailurePolicy.ManualFailure, refused.ReasonCode);
        Assert.Equal(BlockedReleaseStates.Refused, refused.State);
        Assert.Equal(world.EntityId, refused.EntityId);
        // #874: "It's bad" refuses the release from every indexer, not only
        // the one it was downloaded from.
        Assert.True(refused.EveryIndexer);
        Assert.Contains(BlockedReleaseKeys.ForEveryIndexer(Release), await world.Blocklist.ListKeysAsync(CancellationToken.None));

        Assert.Single(await world.Jobs.ListAsync(10, CancellationToken.None));

        var activity = (await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None))
            .Single(entry => entry.Category == "download.queue.removed");
        Assert.Contains("bad", activity.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("blocklisted", activity.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("search", activity.Message, StringComparison.OrdinalIgnoreCase);
        // #799's last line: the removal, blocklist entry and new search this
        // reason triggers are attributed to a person's own action, not an
        // automatic one. Deluno's convention for that attribution is the
        // message's own wording — "You marked/said/told…" — rather than a
        // separate flag on the activity record itself.
        Assert.StartsWith("You marked", activity.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("sabnzbd")]
    public async Task I_dont_want_it_removes_and_deletes_but_never_blocklists_or_searches(string protocol)
    {
        using var world = await World.CreateAsync(protocol);

        var result = await world.RemoveAsync(QueueRemovalReasons.Unwanted);

        Assert.True(result.Succeeded);
        Assert.False(result.Blocklisted);
        Assert.False(result.SearchStarted);
        Assert.Equal(DownloadClientActions.DeleteWithData, world.Client.LastAction);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Empty(await world.Jobs.ListAsync(10, CancellationToken.None));

        // The caller still gets the title back, so the frontend can carry out
        // "unmonitor the title" itself through the existing movie/series bulk
        // endpoint — this reason never touches the blocklist to get there.
        Assert.Equal(world.EntityId, result.EntityId);
        Assert.Equal(world.MediaType, result.MediaType);

        var activity = (await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None))
            .Single(entry => entry.Category == "download.queue.removed");
        Assert.Contains("don't want", activity.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("blocklist", activity.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("sabnzbd")]
    public async Task Just_stop_tracking_it_forgets_without_touching_the_clients_payload(string protocol)
    {
        using var world = await World.CreateAsync(protocol);

        var result = await world.RemoveAsync(QueueRemovalReasons.Forget);

        Assert.True(result.Succeeded);
        Assert.False(result.Blocklisted);
        Assert.False(result.SearchStarted);
        Assert.Equal(DownloadClientActions.Forget, world.Client.LastAction);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Empty(await world.Jobs.ListAsync(10, CancellationToken.None));

        var activity = (await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None))
            .Single(entry => entry.Category == "download.queue.removed");
        Assert.Contains("stop tracking", activity.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("untouched", activity.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unrecognised_reason_is_refused_rather_than_guessed()
    {
        using var world = await World.CreateAsync("qbittorrent");

        var result = await world.RemoveAsync("obliterate");

        Assert.False(result.Succeeded);
        Assert.Null(world.Client.LastAction);
    }

    private sealed class FakeClient(string protocol) : IDownloadClient
    {
        public string Protocol => protocol;

        public string? LastAction { get; private set; }

        public string? LastQueueItemId { get; private set; }

        public DownloadClientTelemetryCapabilities Capabilities { get; } =
            new(true, true, true, true, true, true, protocol);

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
        {
            LastAction = action;
            LastQueueItemId = queueItemId;
            return Task.FromResult(new DownloadClientActionResult(client.Id, queueItemId, action, true, $"{action} sent to {client.Name}"));
        }

        public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
            => nativeStatus ?? "unknown";
    }

    private sealed class PassThroughResiliencePolicy : IIntegrationResiliencePolicy
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

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;
        private readonly DownloadClientTelemetryService _service;

        private World(
            TestStorage storage,
            DownloadClientTelemetryService service,
            DownloadClientItem client,
            FakeClient fakeClient,
            string entityId,
            string mediaType,
            SqliteJobStore jobs,
            SqliteBlockedReleaseRepository blocklist)
        {
            _storage = storage;
            _service = service;
            Client = fakeClient;
            ClientId = client.Id;
            EntityId = entityId;
            MediaType = mediaType;
            Jobs = jobs;
            Activity = jobs;
            Blocklist = blocklist;
        }

        public FakeClient Client { get; }

        public string ClientId { get; }

        public string EntityId { get; }

        public string MediaType { get; }

        public SqliteJobStore Jobs { get; }

        public IActivityFeedRepository Activity { get; }

        public SqliteBlockedReleaseRepository Blocklist { get; }

        public static async Task<World> CreateAsync(string protocol)
        {
            var storage = TestStorage.Create();
            var now = DateTimeOffset.Parse("2026-09-27T09:00:00Z");
            var time = new FixedTimeProvider(now);
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            var settings = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var libraries = new SqliteLibrariesRepository(storage.Factory, time);
            var mediaType = protocol == "sabnzbd" ? "movie" : "tv";
            var library = await libraries.CreateLibraryAsync(new CreateLibraryRequest(
                "Library", mediaType, "library", Path.Combine(storage.DataRoot, "media"), Path.Combine(storage.DataRoot, "downloads"), null,
                "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
            var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
                protocol, protocol, "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var entityType = mediaType == "tv" ? "series" : "movie";
            var entityId = $"{entityType}-1";
            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, mediaType, entityType, entityId, Release, Indexer, client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(dispatchId, QueueItemId, null, completed: false, CancellationToken.None);

            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var fakeClient = new FakeClient(protocol);
            var registry = new DownloadClientRegistry([fakeClient]);
            var service = new DownloadClientTelemetryService(
                settings,
                new SqliteDownloadHealthRepository(storage.Factory, time),
                libraries,
                connections,
                null!,
                registry,
                time,
                new PassThroughResiliencePolicy(),
                jobs,
                dispatches,
                jobs,
                new Deluno.Recovery.Policies.DownloadHealthEvaluator(),
                null!,
                Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance,
                blocklist);

            return new World(storage, service, client, fakeClient, entityId, mediaType, jobs, blocklist);
        }

        public Task<QueueRemovalResult> RemoveAsync(string reason)
            => _service.RemoveWithReasonAsync(ClientId, QueueItemId, reason, CancellationToken.None);

        public void Dispose() => _storage.Dispose();
    }
}
