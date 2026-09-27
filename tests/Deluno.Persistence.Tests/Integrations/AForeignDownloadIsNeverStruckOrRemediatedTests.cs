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
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Recovery.Policies;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// Cleanup sheet row M2 (#933): a download nobody at Deluno sent is never
/// struck, blocklisted, replaced, removed or deleted.
///
/// <para>Before this, <c>AttachHealthFindingsAsync</c> ran the health
/// evaluator and wrote a strike record for every item a client reported,
/// Deluno's own or not - so a stalled torrent someone else added by hand
/// could accumulate strikes in Deluno's own health table and, if it happened
/// to reach the threshold, be swept up by
/// <c>RunConfiguredHealthRemediationAsync</c> exactly like a release Deluno
/// grabbed itself. This is the end-to-end guard: a client reporting one
/// Deluno download and one foreign download, both stalled, both driven
/// through the real <c>GetOverviewAsync</c>/<c>RunConfiguredHealthRemediationAsync</c>
/// pipeline.</para>
/// </summary>
public sealed class AForeignDownloadIsNeverStruckOrRemediatedTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T12:00:00Z");
    private const string DelunoItemId = "deluno-item-1";
    private const string ForeignItemId = "foreign-item-1";
    private const string HandAddedInCategoryItemId = "hand-added-in-category-1";

    [Fact]
    public async Task A_foreign_stalled_item_gets_no_strike_and_is_never_remediated()
    {
        using var world = await World.CreateAsync();

        var overview = await world.Service.GetOverviewAsync(CancellationToken.None);

        // The Queue itself only ever shows Deluno's own download (#933's
        // first acceptance line, already covered elsewhere) - restated here
        // because the rest of this test depends on it.
        var snapshot = Assert.Single(overview.Clients);
        Assert.Single(snapshot.Queue, item => item.Id == DelunoItemId);
        Assert.DoesNotContain(snapshot.Queue, item => item.Id == ForeignItemId);
        // #933: a download added by hand straight into Deluno's own category
        // matches no dispatch Deluno ever recorded, so it is left off the
        // Queue exactly like a download in an unrelated category is -
        // category alone was the defect this fixed.
        Assert.DoesNotContain(snapshot.Queue, item => item.Id == HandAddedInCategoryItemId);

        // No strike record exists for the foreign item, though it stalled
        // exactly like Deluno's own download did.
        var records = await world.Health.ListDownloadHealthRecordsPageAsync(new PageRequest(50, null), CancellationToken.None);
        Assert.DoesNotContain(records.Items, record => record.QueueItemId == ForeignItemId);
        Assert.DoesNotContain(records.Items, record => record.QueueItemId == HandAddedInCategoryItemId);
        // The guard is selective, not simply broken: Deluno's own stalled
        // download still gets struck.
        Assert.Contains(records.Items, record => record.QueueItemId == DelunoItemId);

        // And remediation - blocklisting, a replacement search, removing the
        // client entry - never reaches the foreign item, because it was never
        // struck and is not in the Queue this pass acts on.
        var report = await world.Service.RunConfiguredHealthRemediationAsync(overview, CancellationToken.None);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.DoesNotContain(
            await world.Jobs.ListAsync(100, CancellationToken.None),
            job => job.RelatedEntityId == ForeignItemId);
        Assert.DoesNotContain(
            await world.Jobs.ListAsync(100, CancellationToken.None),
            job => job.RelatedEntityId == HandAddedInCategoryItemId);
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;

        private World(TestStorage storage, DownloadClientTelemetryService service, SqliteJobStore jobs, SqliteBlockedReleaseRepository blocklist, SqliteDownloadHealthRepository health)
        {
            _storage = storage;
            Service = service;
            Jobs = jobs;
            Blocklist = blocklist;
            Health = health;
        }

        public DownloadClientTelemetryService Service { get; }

        public SqliteJobStore Jobs { get; }

        public SqliteBlockedReleaseRepository Blocklist { get; }

        public SqliteDownloadHealthRepository Health { get; }

        public static async Task<World> CreateAsync()
        {
            var storage = TestStorage.Create();
            var time = new FixedTimeProvider(Now);
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
                "External qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "deluno-movies", "deluno-tv", null, 1, true), CancellationToken.None);
            await connections.UpdateDownloadClientHealthAsync(client.Id, "healthy", "Connected.", null, 5, CancellationToken.None);

            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-arrival", "Arrival.2016.1080p.WEB", "Fixture", client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None);
            // Deluno's own download is claimed the same way #931's cleanup
            // reads ownership elsewhere: the client handed back an item id
            // that matches what Deluno recorded when it grabbed the release.
            await dispatches.RecordDetectionAsync(dispatchId, DelunoItemId, 1024, completed: false, CancellationToken.None);

            var health = new SqliteDownloadHealthRepository(storage.Factory, time);
            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var processors = new SqliteProcessorRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var resilience = new IntegrationResiliencePolicy(time, storage.Factory, NullLogger<IntegrationResiliencePolicy>.Instance);
            var registry = new DownloadClientRegistry([new StalledQueueClient(client, time.GetUtcNow())]);

            var service = new DownloadClientTelemetryService(
                settings, health, libraries, connections, jobs, registry, time, resilience,
                jobs, dispatches, jobs, new DownloadHealthEvaluator(), processors,
                Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance, blocklist);

            return new World(storage, service, jobs, blocklist, health);
        }

        public void Dispose() => _storage.Dispose();
    }

    /// <summary>
    /// A client reporting three stalled downloads: one Deluno sent (its item
    /// id matches a recorded dispatch), one added by another tool or by hand
    /// in a category Deluno does not use, and one added by hand straight into
    /// the category Deluno itself uses for movies - the #933 defect: nothing
    /// Deluno recorded mentions it, so the category it happens to share with
    /// Deluno's own downloads must not be enough to claim it.
    /// </summary>
    private sealed class StalledQueueClient(DownloadClientItem client, DateTimeOffset addedUtc) : IDownloadClient
    {
        public string Protocol => "qbittorrent";

        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, true, true, "none");

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem requested, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
        {
            var delunoItem = new DownloadQueueItem(
                DelunoItemId, client.Id, client.Name, Protocol, "movies", "Arrival", "Arrival.2016.1080p.WEB",
                "deluno-movies", DownloadQueueStatuses.Stalled, 0.4, 0, 0, 1024, 400, 0, "Fixture", null, addedUtc);
            var foreignItem = new DownloadQueueItem(
                ForeignItemId, client.Id, client.Name, Protocol, "movies", "Someone Else's Download", "Someone.Elses.Download.2026.1080p.WEB",
                "manual-downloads", DownloadQueueStatuses.Stalled, 0.4, 0, 0, 1024, 400, 0, "Fixture", null, addedUtc);
            var handAddedInCategoryItem = new DownloadQueueItem(
                HandAddedInCategoryItemId, client.Id, client.Name, Protocol, "movies", "Added By Hand", "Added.By.Hand.2026.1080p.WEB",
                "deluno-movies", DownloadQueueStatuses.Stalled, 0.4, 0, 0, 1024, 400, 0, "Fixture", null, addedUtc);

            return Task.FromResult<DownloadClientTelemetrySnapshot?>(new DownloadClientTelemetrySnapshot(
                client.Id, client.Name, Protocol, null, "healthy", null,
                Capabilities,
                DownloadQueueSummary.Of([delunoItem, foreignItem, handAddedInCategoryItem]),
                [delunoItem, foreignItem, handAddedInCategoryItem],
                [],
                capturedUtc));
        }

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem requested, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem requested, string action, string queueItemId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem requested, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DownloadClientHistoryItem>>([]);

        public Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem requested, string category, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem requested, string queueItemId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>?>(null);

        public string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
            => nativeStatus ?? "unknown";
    }
}
