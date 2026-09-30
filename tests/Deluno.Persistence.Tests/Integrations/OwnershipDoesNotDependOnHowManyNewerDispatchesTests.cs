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
using Deluno.Recovery.Policies;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #1062: which downloads are Deluno's is decided by asking about the exact
/// ids the client reports (archived dispatches included), not by reading the
/// newest 200 dispatches and hoping the right one is among them. A torrent
/// Deluno sent months ago that is still seeding must stay in Transfers, in the
/// counts and in the sharing rule, however many dispatches came after it.
/// </summary>
public sealed class OwnershipDoesNotDependOnHowManyNewerDispatchesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
    private const string OldSeedingHash = "OLD-SEEDING-HASH";
    private const string OutsideHash = "outside-hash";

    [Fact]
    public async Task An_old_archived_torrent_still_seeding_stays_Delunos_after_hundreds_of_newer_dispatches()
    {
        using var world = await World.CreateAsync(newerDispatches: 260);

        var overview = await world.Service.GetOverviewAsync(CancellationToken.None);

        var client = Assert.Single(overview.Clients);
        // Shown (the id is matched ignoring case) and the outside item is not.
        var shown = Assert.Single(client.Queue);
        Assert.Equal(OldSeedingHash.ToLowerInvariant(), shown.Id);
    }

    [Fact]
    public async Task The_screen_and_the_remove_guard_agree()
    {
        using var world = await World.CreateAsync(newerDispatches: 260);

        var overview = await world.Service.GetOverviewAsync(CancellationToken.None);
        var listed = Assert.Single(Assert.Single(overview.Clients).Queue);
        var own = await world.Service.CheckOwnershipAsync(world.ClientId, listed.Id, CancellationToken.None);
        var outside = await world.Service.CheckOwnershipAsync(world.ClientId, OutsideHash, CancellationToken.None);

        Assert.True(own.Owned);
        Assert.False(outside.Owned);
    }

    [Fact]
    public async Task Asking_for_ids_returns_archived_dispatches_ignoring_case_beyond_any_window()
    {
        using var world = await World.CreateAsync(newerDispatches: 260);

        var found = await world.Dispatches.FindDispatchesByClientItemIdsAsync(
            world.ClientId, [OldSeedingHash.ToLowerInvariant(), "nothing"], CancellationToken.None);

        var dispatch = Assert.Single(found);
        Assert.Equal("archived", dispatch.Status);
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;

        private World(TestStorage storage, DownloadClientTelemetryService service, SqliteDownloadDispatchesRepository dispatches, string clientId)
        {
            _storage = storage;
            Service = service;
            Dispatches = dispatches;
            ClientId = clientId;
        }

        public DownloadClientTelemetryService Service { get; }

        public SqliteDownloadDispatchesRepository Dispatches { get; }

        public string ClientId { get; }

        public static async Task<World> CreateAsync(int newerDispatches)
        {
            var storage = TestStorage.Create();
            var time = new FixedTimeProvider(Now.AddDays(-90));
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

            // Sent three months ago, stored in capitals, since finished and archived.
            var old = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-old", "Old.Seeding.Movie.2026.1080p.WEB", "Fixture", client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(old, OldSeedingHash, 1024, completed: true, CancellationToken.None);
            await dispatches.ArchiveDispatchAsync(old, "imported long ago", CancellationToken.None);

            // Hundreds of dispatches on the same client since.
            time.Advance(TimeSpan.FromDays(89));
            for (var index = 0; index < newerDispatches; index++)
            {
                time.Advance(TimeSpan.FromMinutes(1));
                await jobs.RecordDownloadDispatchAsync(
                    library.Id, "movies", "movie", $"movie-{index}", $"Newer.Movie.{index}.2026.1080p.WEB", "Fixture", client.Id, client.Name, "sent", null,
                    cancellationToken: CancellationToken.None,
                    clientExternalId: $"newer-{index}");
            }

            var health = new SqliteDownloadHealthRepository(storage.Factory, time);
            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var alerts = new SqliteDispatchAlertRepository(storage.Factory, time);
            var processors = new SqliteProcessorRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var resilience = new IntegrationResiliencePolicy(time, storage.Factory, NullLogger<IntegrationResiliencePolicy>.Instance);
            var registry = new DownloadClientRegistry([new Client(client)]);

            var service = new DownloadClientTelemetryService(
                settings, health, libraries, connections, jobs, registry, time, resilience,
                jobs, dispatches, jobs, new DownloadHealthEvaluator(), processors,
                Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance, blocklist,
                dispatchAlertRepository: alerts);

            return new World(storage, service, dispatches, client.Id);
        }

        public void Dispose() => _storage.Dispose();
    }

    private sealed class Client(DownloadClientItem client) : IDownloadClient
    {
        public string Protocol => "qbittorrent";

        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, true, true, "none");

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem requested, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
        {
            DownloadQueueItem Item(string id, string release) => new(
                id, client.Id, client.Name, Protocol, "movies", release, release,
                "deluno-movies", DownloadQueueStatuses.Completed, 1.0, 0, 0, 1024, 1024, 0, "Fixture", null, Now.AddDays(-90),
                SourcePath: $"C:/dl/{release}");

            var queue = new List<DownloadQueueItem>
            {
                Item(OldSeedingHash.ToLowerInvariant(), "Old.Seeding.Movie.2026.1080p.WEB"),
                Item(OutsideHash, "Someone.Elses.Download.2026.1080p.WEB")
            };
            return Task.FromResult<DownloadClientTelemetrySnapshot?>(new DownloadClientTelemetrySnapshot(
                client.Id, client.Name, Protocol, null, "healthy", null,
                Capabilities,
                DownloadQueueSummary.Of(queue),
                queue,
                [],
                capturedUtc));
        }

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem requested, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem requested, string action, string queueItemId, CancellationToken cancellationToken)
            => Task.FromResult(new DownloadClientActionResult(requested.Id, queueItemId, action, true, "done"));

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
