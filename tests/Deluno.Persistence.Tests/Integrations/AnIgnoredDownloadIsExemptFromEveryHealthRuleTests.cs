using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Contracts.Connectivity;
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
/// #817: a download matching an ignore-list entry - by hash, download-client
/// category, or tracker/indexer domain - is exempt from every download-health
/// rule. It never gets a finding, so it never strikes, blocklists, removes,
/// or reclaims, whatever any enabled rule would otherwise do with it.
/// </summary>
public sealed class AnIgnoredDownloadIsExemptFromEveryHealthRuleTests
{
    private const string Release = "Dune.Part.Two.2024.2160p.WEB-DL-DELUNO";
    private const string QueueItemId = "queue-dune";
    private const string Category = "deluno-movies";
    private const string IndexerName = "PrivateHD";

    [Fact]
    public async Task A_download_matching_by_hash_gets_no_finding()
    {
        using var world = await World.CreateAsync();
        await world.IgnoreAsync(new DownloadHealthIgnoreEntry("entry-1", Hash: QueueItemId, Category: null, TrackerDomain: null, Note: null, CreatedUtc: default));

        var overview = await world.PollAsync();

        Assert.Empty(Assert.Single(Assert.Single(overview.Clients).Queue).HealthFindings!);
    }

    [Fact]
    public async Task A_download_matching_by_client_category_gets_no_finding()
    {
        using var world = await World.CreateAsync();
        await world.IgnoreAsync(new DownloadHealthIgnoreEntry("entry-1", Hash: null, Category: Category, TrackerDomain: null, Note: null, CreatedUtc: default));

        var overview = await world.PollAsync();

        Assert.Empty(Assert.Single(Assert.Single(overview.Clients).Queue).HealthFindings!);
    }

    [Fact]
    public async Task A_download_matching_by_tracker_domain_gets_no_finding()
    {
        using var world = await World.CreateAsync();
        await world.IgnoreAsync(new DownloadHealthIgnoreEntry("entry-1", Hash: null, Category: null, TrackerDomain: IndexerName, Note: null, CreatedUtc: default));

        var overview = await world.PollAsync();

        Assert.Empty(Assert.Single(Assert.Single(overview.Clients).Queue).HealthFindings!);
    }

    [Fact]
    public async Task A_download_matching_nothing_on_the_ignore_list_still_gets_its_finding()
    {
        using var world = await World.CreateAsync();
        await world.IgnoreAsync(new DownloadHealthIgnoreEntry("entry-1", Hash: "some-other-hash", Category: "some-other-category", TrackerDomain: "some-other-tracker", Note: null, CreatedUtc: default));

        var overview = await world.PollAsync();

        Assert.NotEmpty(Assert.Single(Assert.Single(overview.Clients).Queue).HealthFindings!);
    }

    private sealed class StalledClient(string protocol, string clientId, string clientName) : IDownloadClient
    {
        public string Protocol => protocol;

        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, true, true, "none");

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => Task.FromResult<DownloadClientTelemetrySnapshot?>(new DownloadClientTelemetrySnapshot(
                clientId, clientName, protocol, "http://127.0.0.1:8080", "healthy", null,
                Capabilities,
                new DownloadTelemetrySummary(0, 0, 0, 1, 0, 0, 0),
                [new DownloadQueueItem(
                    QueueItemId, clientId, clientName, protocol, "movies", "Dune Part Two", Release,
                    Category, DownloadQueueStatuses.Stalled, 42, 0, 0, 4_000_000_000, 1_600_000_000, 0, IndexerName, "Queue status: stalled.",
                    capturedUtc.AddMinutes(-31))],
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
            => nativeStatus ?? "unknown";
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;
        private readonly DownloadClientTelemetryService _service;
        private readonly SqliteDownloadHealthRepository _healthRepository;

        private World(TestStorage storage, DownloadClientTelemetryService service, SqliteDownloadHealthRepository healthRepository)
        {
            _storage = storage;
            _service = service;
            _healthRepository = healthRepository;
        }

        public static async Task<World> CreateAsync()
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
                "qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "deluno-movies", "deluno-tv", null, 1, true), CancellationToken.None);

            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var processors = new SqliteProcessorRepository(storage.Factory, time, secretProtection);
            var resiliencePolicy = new IntegrationResiliencePolicy(time, storage.Factory, NullLogger<IntegrationResiliencePolicy>.Instance);

            // Deluno itself grabbed this release (#933): the Queue only shows
            // downloads Deluno recorded dispatching, matched by the id the
            // client handed back.
            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-dune", Release, "Fixture Indexer",
                client.Id, client.Name, "sent", null, cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(dispatchId, QueueItemId, 4_000_000_000, completed: false, CancellationToken.None);

            var fakeClient = new StalledClient("qbittorrent", client.Id, client.Name);
            var registry = new DownloadClientRegistry([fakeClient]);
            var healthRepository = new SqliteDownloadHealthRepository(storage.Factory, time);

            var service = new DownloadClientTelemetryService(
                settings, healthRepository, libraries, connections,
                jobs, registry, time, resiliencePolicy, jobs, dispatches, jobs,
                new DownloadHealthEvaluator(), processors, NullConnectivityMonitor.Instance);

            return new World(storage, service, healthRepository);
        }

        public Task IgnoreAsync(DownloadHealthIgnoreEntry entry)
            => _healthRepository.AddDownloadHealthIgnoreEntryAsync(entry, CancellationToken.None);

        public Task<DownloadTelemetryOverview> PollAsync() => _service.GetOverviewAsync(CancellationToken.None);

        public void Dispose() => _storage.Dispose();
    }
}
