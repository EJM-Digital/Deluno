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
/// #981: the sharing pass (<c>WorkPlanner.PlanSharingReclaimAsync</c>) already
/// checks the real files and knows which downloads lost their hardlink into
/// the library — upgraded or deleted out from under a still-seeding item. This
/// covers that <c>DownloadClientTelemetryService</c> folds that fact onto the
/// matching Transfers row as a plain, distinct <see cref="DownloadHealthFinding"/>
/// ("Unlinked from your library"), never confused with a missing library file:
/// this download is still on disk and safe to reclaim, a missing library file
/// has nothing to reclaim from.
/// </summary>
public sealed class AHardlinkLostHealthFindingTests
{
    private const string Release = "Arrival.2016.WEB.1080p-DELUNO";

    [Fact]
    public async Task A_lost_hardlink_appears_as_a_distinct_finding_on_Delunos_own_queue_item()
    {
        using var world = await World.CreateAsync(lostHardlink: true);

        var overview = await world.PollAsync();

        var item = Assert.Single(Assert.Single(overview.Clients).Queue);
        var finding = Assert.Single(item.HealthFindings!, finding => finding.Kind == "hardlink-lost");
        Assert.Equal("warning", finding.Severity);
        Assert.Equal("Unlinked from your library", finding.Summary);
        Assert.DoesNotContain(item.HealthFindings!, finding => finding.Kind == "missingTrackedFile" || finding.Kind == "missing-library-file");
    }

    [Fact]
    public async Task An_intact_hardlink_means_no_finding()
    {
        using var world = await World.CreateAsync(lostHardlink: false);

        var overview = await world.PollAsync();

        var item = Assert.Single(Assert.Single(overview.Clients).Queue);
        Assert.DoesNotContain(item.HealthFindings ?? [], finding => finding.Kind == "hardlink-lost");
    }

    private sealed class StillDownloadingClient(string clientId, string clientName, Func<DownloadQueueItem> queueItemFactory) : IDownloadClient
    {
        public string Protocol => "qbittorrent";

        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, false, true, "api-key");

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => Task.FromResult<DownloadClientTelemetrySnapshot?>(new DownloadClientTelemetrySnapshot(
                clientId, clientName, "torrent", "http://127.0.0.1:8080", "healthy", null,
                Capabilities,
                new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
                [queueItemFactory()],
                [],
                capturedUtc));

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DownloadClientHistoryItem>>([]);

        public Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>?>(null);

        public string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
            => nativeStatus ?? "downloading";
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;
        private readonly DownloadClientTelemetryService _service;

        private World(TestStorage storage, DownloadClientTelemetryService service)
        {
            _storage = storage;
            _service = service;
        }

        public static async Task<World> CreateAsync(bool lostHardlink)
        {
            var storage = TestStorage.Create();
            var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-27T09:00:00Z"));
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            var secretProtection = TestSecretProtection.Create(storage);
            var settings = new SqlitePlatformSettingsRepository(storage.Factory, time, secretProtection);
            var connections = new SqliteConnectionsRepository(storage.Factory, time, secretProtection);
            var libraries = new SqliteLibrariesRepository(storage.Factory, time);
            var library = await libraries.CreateLibraryAsync(new CreateLibraryRequest(
                "Movies", "movies", "library", Path.Combine(storage.DataRoot, "movies"), Path.Combine(storage.DataRoot, "downloads"), null,
                "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
            var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
                "qBittorrent", "qbittorrent", "localhost", 8080, null, "api-key", null, "deluno-movies", "deluno-tv", null, 1, true), CancellationToken.None);

            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var processors = new SqliteProcessorRepository(storage.Factory, time, secretProtection);
            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var resiliencePolicy = new IntegrationResiliencePolicy(time, storage.Factory, NullLogger<IntegrationResiliencePolicy>.Instance);
            var sharing = new SqliteDownloadSharingRepository(storage.Factory, time);

            // #933: the Queue only shows downloads Deluno recorded dispatching.
            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-arrival", Release, "Fixture Indexer",
                client.Id, client.Name, "sent", null, cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(dispatchId, "queue-arrival", 2_000_000_000, completed: false, CancellationToken.None);

            if (lostHardlink)
            {
                await sharing.ReplaceHoldsAsync(
                    [
                        new DownloadSharingHold(
                            client.Id,
                            client.Name,
                            "queue-arrival",
                            "Arrival",
                            "No longer shared with your library - the library copy was upgraded or removed.",
                            2_000_000_000,
                            NeedsYou: false,
                            SharesLibraryCopy: false,
                            LostHardlink: true)
                    ],
                    driveNote: null,
                    CancellationToken.None);
            }

            var fakeClient = new StillDownloadingClient(client.Id, client.Name, () => new DownloadQueueItem(
                "queue-arrival", client.Id, client.Name, "torrent", "movies", "Arrival", Release,
                "deluno-movies", DownloadQueueStatuses.Downloading, 42, 5, 600, 2_000_000_000, 840_000_000, 0, "Fixture", null,
                time.GetUtcNow(), LibraryId: library.Id));
            var registry = new DownloadClientRegistry([fakeClient]);

            var service = new DownloadClientTelemetryService(
                settings, new SqliteDownloadHealthRepository(storage.Factory, time), libraries, connections,
                jobs, registry, time, resiliencePolicy, jobs, dispatches, jobs,
                new DownloadHealthEvaluator(), processors, Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance, blocklist,
                sharingRepository: sharing);

            return new World(storage, service);
        }

        public Task<DownloadTelemetryOverview> PollAsync() => _service.GetOverviewAsync(CancellationToken.None);

        public void Dispose() => _storage.Dispose();
    }
}
