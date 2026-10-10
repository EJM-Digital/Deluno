using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
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
/// #927: a download that vanishes from the client entirely - gone from both
/// queue and history - is told once, in plain words, and never blocklisted
/// or searched for automatically: it may have been removed on purpose.
/// </summary>
public sealed class AVanishedDownloadIsAlertedOnceTests
{
    private const string Release = "Sintel.2010.1080p.WEB-DL-DELUNO";

    [Fact]
    public async Task A_download_gone_from_the_client_entirely_is_alerted_and_recorded_in_activity()
    {
        using var world = await World.CreateAsync();

        // The client's own next poll lists something else entirely - this
        // dispatch's item is gone from both its queue and its history.
        await world.PollWithOtherItemInQueueAsync();

        Assert.Equal(1, await world.Problems.CountOpenAsync(CancellationToken.None));
        Assert.True(await world.Problems.IsRecordedAsync(world.DispatchId, "vanished-downloading", CancellationToken.None));

        var activities = await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None);
        var activity = Assert.Single(activities, item => item.Category == "download.vanished");
        Assert.Contains("vanished", activity.Message, StringComparison.OrdinalIgnoreCase);

        // No automatic action: it may have been removed on purpose (#927).
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Empty(await world.Jobs.ListAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task The_same_vanished_download_is_not_alerted_twice()
    {
        using var world = await World.CreateAsync();

        await world.PollWithOtherItemInQueueAsync();
        await world.PollWithOtherItemInQueueAsync();

        Assert.Equal(1, await world.Problems.CountOpenAsync(CancellationToken.None));
        var activities = await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None);
        Assert.Single(activities, item => item.Category == "download.vanished");
    }

    [Fact]
    public async Task An_entirely_empty_poll_does_not_alert_it_as_vanished()
    {
        using var world = await World.CreateAsync();

        await world.PollWithEmptyQueueAndHistoryAsync();

        Assert.Equal(0, await world.Problems.CountOpenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_download_still_present_is_not_alerted()
    {
        using var world = await World.CreateAsync();

        await world.PollWithItsOwnItemStillInQueueAsync();

        Assert.Equal(0, await world.Problems.CountOpenAsync(CancellationToken.None));
    }

    /// <summary>
    /// #927: the recovery case (Search for another / Stop wanting it /
    /// Dismiss) is a separate signal from the alert - this is what puts the
    /// three buttons on the Transfers screen.
    /// </summary>
    [Fact]
    public async Task A_vanished_download_also_raises_its_own_recovery_case()
    {
        using var world = await World.CreateAsync();

        await world.PollWithOtherItemInQueueAsync();

        var call = Assert.Single(world.RecoveryHandler.VanishedCalls);
        Assert.Equal((Release, "movies", world.ClientId, $"qBittorrent on {Deluno.Contracts.DelunoHostName.Current}", Release, "movie-sintel"), (call.Title, call.MediaType, call.ClientId, call.ClientName, call.ReleaseName, call.EntityId));
        Assert.Contains(world.DispatchId, call.DetailsJson);
    }

    /// <summary>
    /// #1167: a removal Deluno made itself (the stall rules, Remove failed, a
    /// person pressing Remove in Transfers) is never then reported as the
    /// download having vanished from the client.
    /// </summary>
    [Fact]
    public async Task A_download_Deluno_removed_itself_is_not_then_reported_as_vanished()
    {
        using var world = await World.CreateAsync();

        await world.RemoveFromClientAsync();
        await world.PollWithOtherItemInQueueAsync();

        Assert.Equal(0, await world.Problems.CountOpenAsync(CancellationToken.None));
        Assert.Empty(world.RecoveryHandler.VanishedCalls);
        var activities = await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None);
        Assert.DoesNotContain(activities, item => item.Category == "download.vanished");
    }

    [Fact]
    public async Task A_download_the_user_removed_in_the_client_itself_still_raises_vanished()
    {
        using var world = await World.CreateAsync();

        await world.PollWithOtherItemInQueueAsync();

        Assert.Equal(1, await world.Problems.CountOpenAsync(CancellationToken.None));
        Assert.Single(world.RecoveryHandler.VanishedCalls);
    }

    private sealed class RemovalOnlyClient : IDownloadClient
    {
        public string Protocol => "qbittorrent";

        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, true, true, "qbittorrent");

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
            => Task.FromResult(new DownloadClientActionResult(client.Id, queueItemId, action, true, $"{action} sent to {client.Name}"));

        public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>?>(null);

        public string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
            => nativeStatus ?? "unknown";
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;
        private readonly DownloadClientTelemetryService _service;
        private readonly DownloadClientItem _client;

        private World(
            TestStorage storage,
            DateTimeOffset now,
            DownloadClientTelemetryService service,
            DownloadClientItem client,
            string dispatchId,
            SqliteJobStore jobs,
            SqliteDownloadDispatchesRepository dispatches,
            SqliteBlockedReleaseRepository blocklist,
            SqliteDispatchProblemLog problems,
            FakeDispatchRecoveryHandler recoveryHandler)
        {
            _storage = storage;
            Now = now;
            _service = service;
            _client = client;
            DispatchId = dispatchId;
            Jobs = jobs;
            Dispatches = dispatches;
            Blocklist = blocklist;
            Problems = problems;
            Activity = jobs;
            RecoveryHandler = recoveryHandler;
        }

        public DateTimeOffset Now { get; }

        public string DispatchId { get; }

        public string ClientId => _client.Id;

        public SqliteJobStore Jobs { get; }

        public SqliteDownloadDispatchesRepository Dispatches { get; }

        public SqliteBlockedReleaseRepository Blocklist { get; }

        public SqliteDispatchProblemLog Problems { get; }

        public FakeDispatchRecoveryHandler RecoveryHandler { get; }

        public Deluno.Jobs.Data.IActivityFeedRepository Activity { get; }

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
            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-sintel", Release, "Fixture", client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None);
            // The id the client gave this grab, and Deluno has already seen it downloading.
            await dispatches.RecordDetectionAsync(dispatchId, "hash-1", 1_000_000, completed: false, CancellationToken.None);
            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var problems = new SqliteDispatchProblemLog(storage.Factory, time);
            var recoveryHandler = new FakeDispatchRecoveryHandler();
            var service = new DownloadClientTelemetryService(
                settings, new SqliteDownloadHealthRepository(storage.Factory, time), libraries, connections, null!, new DownloadClientRegistry([new RemovalOnlyClient()]), time, null!,
                jobs, dispatches, jobs, new Deluno.Recovery.Policies.DownloadHealthEvaluator(), null!, Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance,
                blocklist, dispatchProblemLog: problems, recoveryHandler: recoveryHandler);
            return new World(storage, now, service, client, dispatchId, jobs, dispatches, blocklist, problems, recoveryHandler);
        }

        public Task RemoveFromClientAsync() => _service.ReclaimCompletedAsync(_client.Id, "hash-1", CancellationToken.None);

        public Task ForgetFromClientAsync() => _service.ExecuteActionAsync(_client.Id, new DownloadClientActionRequest(DownloadClientActions.RemoveRefusedCopy, "hash-1"), CancellationToken.None);

        public Task PollWithOtherItemInQueueAsync() => PollAsync(queue: [Queued("hash-other")], history: []);

        public Task PollWithEmptyQueueAndHistoryAsync() => PollAsync(queue: [], history: []);

        public Task PollWithItsOwnItemStillInQueueAsync() => PollAsync(queue: [Queued("hash-1")], history: []);

        private async Task PollAsync(IReadOnlyList<DownloadQueueItem> queue, IReadOnlyList<DownloadClientHistoryItem> history)
        {
            var snapshot = new DownloadClientTelemetrySnapshot(
                _client.Id, _client.Name, _client.Protocol, null, "healthy", null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, false, true, "basic"),
                new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
                queue, history, Now);
            var clientDispatches = new[] { (await Dispatches.GetDispatchIncludingArchivedAsync(DispatchId, CancellationToken.None))! };
            await _service.RecordWhatTheClientShowsAsync(snapshot, clientDispatches, CancellationToken.None);
        }

        private DownloadQueueItem Queued(string id)
            => new(id, _client.Id, _client.Name, "torrent", "movies", "Something Else", "Something.Else.2020.1080p.WEB-DL-DELUNO",
                "movies", DownloadQueueStatuses.Downloading, 50, 0, 0, 1024, 100, 0, "Fixture", null, Now);

        public void Dispose() => _storage.Dispose();
    }

    private sealed class FakeDispatchRecoveryHandler : IDispatchRecoveryHandler
    {
        public List<(string Title, string MediaType, string ClientId, string ClientName, string ReleaseName, string EntityId, string DetailsJson)> VanishedCalls { get; } = [];

        public Task HandleGrabTimeoutAsync(string title, string mediaType, string downloadClientId, string downloadClientName, string releaseName, string detailsJson, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task HandleDetectionTimeoutAsync(string title, string mediaType, string downloadClientId, string downloadClientName, string releaseName, string entityId, string detailsJson, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task HandleImportTimeoutAsync(string title, string mediaType, string downloadClientId, string downloadClientName, string releaseName, string entityId, string detailsJson, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task HandleImportFailureAsync(string title, string mediaType, string downloadClientId, string downloadClientName, string releaseName, string entityId, string importFailureCode, string importFailureMessage, string detailsJson, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task HandleVanishedDownloadingAsync(string title, string mediaType, string downloadClientId, string downloadClientName, string releaseName, string entityId, string detailsJson, CancellationToken cancellationToken)
        {
            VanishedCalls.Add((title, mediaType, downloadClientId, downloadClientName, releaseName, entityId, detailsJson));
            return Task.CompletedTask;
        }
    }
}
