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
/// #810/#933: queue priority/reorder and force-start act only on a download
/// Deluno itself dispatched — the same rule that already scopes what the
/// Queue shows and acts on. An item merely sharing the client (added by
/// hand, or by another app pointed at the same client) is refused rather
/// than reordered or force-started, even though pause/resume/delete remain
/// unaffected: those already existed before #810 and this gate only covers
/// the new verbs.
/// </summary>
public sealed class AQueueReorderActsOnlyOnDelunosOwnDownloadsTests
{
    private const string Release = "Some.Movie.2024.1080p.WEB-DL-GROUP";

    [Fact]
    public async Task Move_to_top_is_refused_for_a_queue_item_Deluno_never_dispatched()
    {
        using var world = await World.CreateAsync();

        var result = await world.ExecuteActionAsync(DownloadClientActions.MoveToTop, "not-a-deluno-item");

        Assert.False(result.Succeeded);
        Assert.Contains("did not send", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(world.FakeDownloadClient.LastAction);
    }

    [Fact]
    public async Task Move_to_top_reaches_the_client_for_a_download_Deluno_dispatched()
    {
        using var world = await World.CreateAsync();

        var result = await world.ExecuteActionAsync(DownloadClientActions.MoveToTop, world.QueueItemId);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(DownloadClientActions.MoveToTop, world.FakeDownloadClient.LastAction);
        Assert.Equal(world.QueueItemId, world.FakeDownloadClient.LastQueueItemId);
    }

    [Fact]
    public async Task Force_start_is_refused_for_a_queue_item_Deluno_never_dispatched()
    {
        using var world = await World.CreateAsync();

        var result = await world.ExecuteActionAsync(DownloadClientActions.ForceStart, "not-a-deluno-item");

        Assert.False(result.Succeeded);
        Assert.Null(world.FakeDownloadClient.LastAction);
    }

    /// <summary>
    /// The gate is new for #810's verbs only. Pause already worked against
    /// any item the client holds, dispatched by Deluno or not, and #810 does
    /// not narrow that existing behaviour.
    /// </summary>
    [Fact]
    public async Task Pause_is_not_affected_by_the_new_ownership_gate()
    {
        using var world = await World.CreateAsync();

        var result = await world.ExecuteActionAsync(DownloadClientActions.Pause, "not-a-deluno-item");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(DownloadClientActions.Pause, world.FakeDownloadClient.LastAction);
    }

    private sealed class FakeClient(string protocol) : IDownloadClient
    {
        public string Protocol => protocol;

        public string? LastAction { get; private set; }

        public string? LastQueueItemId { get; private set; }

        public DownloadClientTelemetryCapabilities Capabilities { get; } =
            new(true, true, true, true, true, true, protocol, SupportsQueueReorder: true, SupportsForceStart: true);

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

        public Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>?>(null);

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
        private readonly string _clientId;

        private World(TestStorage storage, DownloadClientTelemetryService service, string clientId, string queueItemId, FakeClient fakeClient)
        {
            _storage = storage;
            _service = service;
            _clientId = clientId;
            QueueItemId = queueItemId;
            FakeDownloadClient = fakeClient;
        }

        public string QueueItemId { get; }

        public FakeClient FakeDownloadClient { get; }

        public Task<DownloadClientActionResult> ExecuteActionAsync(string action, string queueItemId)
            => _service.ExecuteActionAsync(_clientId, new DownloadClientActionRequest(action, queueItemId), CancellationToken.None);

        public static async Task<World> CreateAsync()
        {
            var storage = TestStorage.Create();
            var now = DateTimeOffset.Parse("2026-09-27T04:12:00Z");
            var time = new FixedTimeProvider(now);
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            var settings = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var libraries = new SqliteLibrariesRepository(storage.Factory, time);
            var library = await libraries.CreateLibraryAsync(new CreateLibraryRequest(
                "Movies", "movies", "library", Path.Combine(storage.DataRoot, "movies"), Path.Combine(storage.DataRoot, "downloads"), null,
                "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
            var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
                "qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            const string queueItemId = "abc123hash";
            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-1", Release, "TestIndexer", client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(dispatchId, queueItemId, 1024, completed: false, CancellationToken.None);
            var fakeClient = new FakeClient("qbittorrent");
            var service = new DownloadClientTelemetryService(
                settings, new SqliteDownloadHealthRepository(storage.Factory, time), libraries, connections, null!,
                new Deluno.Integrations.DownloadClients.DownloadClientRegistry([fakeClient]), time, new PassThroughResiliencePolicy(),
                jobs, dispatches, jobs, new Deluno.Recovery.Policies.DownloadHealthEvaluator(), null!, Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance, null);
            return new World(storage, service, client.Id, queueItemId, fakeClient);
        }

        public void Dispose() => _storage.Dispose();
    }
}
