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
/// #813: a SABnzbd queue-level warning (disk-full, a failed unpack, and the
/// like) is not about any one release, so <c>DownloadClientTelemetryService</c>
/// must not run it through the strike/rule engine other findings use -- it
/// would eventually block a candidate for a reason that has nothing to do
/// with that candidate. It still has to reach the Transfers screen's Issues
/// view, though, using the same <see cref="DownloadHealthFinding"/> shape
/// every other finding there uses.
/// </summary>
public sealed class SabnzbdClientWarningHealthFindingTests
{
    private const string Release = "Arrival.2016.WEB.1080p-DELUNO";

    [Fact]
    public async Task A_client_warning_appears_as_a_health_finding_on_Delunos_own_queue_item()
    {
        using var world = await World.CreateAsync(clientWarnings: ["Disk full!"]);

        var overview = await world.PollAsync();

        var item = Assert.Single(Assert.Single(overview.Clients).Queue);
        var finding = Assert.Single(item.HealthFindings!, finding => finding.Kind == "sabnzbd-client-warning");
        Assert.Equal("warning", finding.Severity);
        Assert.Contains("Disk full!", finding.Summary, StringComparison.Ordinal);
        Assert.Contains(world.ClientName, finding.Summary, StringComparison.Ordinal);
        // Never struck or able to block a candidate: it is informational,
        // not a per-release problem.
        Assert.Equal(0, finding.StrikeCount);
        Assert.False(finding.CandidateBlocked);
        Assert.Null(finding.RuleId);
    }

    [Fact]
    public async Task No_warning_means_no_finding()
    {
        using var world = await World.CreateAsync(clientWarnings: null);

        var overview = await world.PollAsync();

        var item = Assert.Single(Assert.Single(overview.Clients).Queue);
        Assert.DoesNotContain(item.HealthFindings ?? [], finding => finding.Kind == "sabnzbd-client-warning");
    }

    private sealed class WarningClient(string clientId, string clientName, Func<DownloadQueueItem> queueItemFactory, IReadOnlyList<string>? clientWarnings) : IDownloadClient
    {
        public string Protocol => "sabnzbd";

        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, false, true, "api-key");

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => Task.FromResult<DownloadClientTelemetrySnapshot?>(new DownloadClientTelemetrySnapshot(
                clientId, clientName, "sabnzbd", "http://127.0.0.1:8080", "healthy", null,
                Capabilities,
                new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
                [queueItemFactory()],
                [],
                capturedUtc)
            {
                ClientWarnings = clientWarnings
            });

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DownloadClientHistoryItem>>([]);

        public Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
            => nativeStatus ?? "downloading";
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;
        private readonly DownloadClientTelemetryService _service;

        private World(TestStorage storage, DownloadClientTelemetryService service, string clientName)
        {
            _storage = storage;
            _service = service;
            ClientName = clientName;
        }

        public string ClientName { get; }

        public static async Task<World> CreateAsync(IReadOnlyList<string>? clientWarnings)
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
                "SABnzbd", "sabnzbd", "localhost", 8080, null, "api-key", null, "deluno-movies", "deluno-tv", null, 1, true), CancellationToken.None);

            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var processors = new SqliteProcessorRepository(storage.Factory, time, secretProtection);
            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var resiliencePolicy = new IntegrationResiliencePolicy(time, storage.Factory, NullLogger<IntegrationResiliencePolicy>.Instance);

            // #933: the Queue only shows downloads Deluno recorded dispatching.
            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-arrival", Release, "Fixture Indexer",
                client.Id, client.Name, "sent", null, cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(dispatchId, "queue-arrival", 2_000_000_000, completed: false, CancellationToken.None);

            var fakeClient = new WarningClient(client.Id, client.Name, () => new DownloadQueueItem(
                "queue-arrival", client.Id, client.Name, "sabnzbd", "movies", "Arrival", Release,
                "deluno-movies", DownloadQueueStatuses.Downloading, 42, 5, 600, 2_000_000_000, 840_000_000, 0, "Fixture", null,
                time.GetUtcNow(), LibraryId: library.Id), clientWarnings);
            var registry = new DownloadClientRegistry([fakeClient]);

            var service = new DownloadClientTelemetryService(
                settings, new SqliteDownloadHealthRepository(storage.Factory, time), libraries, connections,
                jobs, registry, time, resiliencePolicy, jobs, dispatches, jobs,
                new DownloadHealthEvaluator(), processors, Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance, blocklist);

            return new World(storage, service, client.Name);
        }

        public Task<DownloadTelemetryOverview> PollAsync() => _service.GetOverviewAsync(CancellationToken.None);

        public void Dispose() => _storage.Dispose();
    }
}
