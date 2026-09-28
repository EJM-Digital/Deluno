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
/// #824: a person's own reachability guard, separate from #901's built-in
/// outage detector. Off by default; when on and every configured URL fails,
/// the whole poll's strike evaluation is skipped - nothing struck, nothing
/// recorded - rather than adjusted the way an #901 outage adjusts elapsed
/// time, because these URLs are opt-in and their failure must never be read
/// as "every download here just started stalling".
/// </summary>
public sealed class AFailedConnectivityCheckSkipsThePassTests
{
    private const string Release = "Dune.Part.Two.2024.2160p.WEB-DL-DELUNO";
    private const string QueueItemId = "queue-dune";

    private sealed class FakeConnectivityCheck(bool reachable) : IDownloadHealthConnectivityCheck
    {
        public bool Reachable { get; set; } = reachable;

        public int CallCount { get; private set; }

        public Task<bool> AnyReachableAsync(string commaSeparatedUrls, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(Reachable);
        }
    }

    [Fact]
    public async Task A_pass_proceeds_and_strikes_normally_when_at_least_one_url_responds()
    {
        using var world = await World.CreateAsync(new FakeConnectivityCheck(reachable: true), connectivityCheckEnabled: true);

        var overview = await world.PollAsync();

        Assert.NotEmpty(Assert.Single(Assert.Single(overview.Clients).Queue).HealthFindings!);
    }

    [Fact]
    public async Task A_failed_check_produces_no_finding_and_records_no_strike()
    {
        var check = new FakeConnectivityCheck(reachable: false);
        using var world = await World.CreateAsync(check, connectivityCheckEnabled: true);

        var overview = await world.PollAsync();

        Assert.Equal(1, check.CallCount);
        Assert.Empty(Assert.Single(Assert.Single(overview.Clients).Queue).HealthFindings!);
        Assert.Empty(await world.HealthRepository.ListDownloadHealthRecordsAsync(10, CancellationToken.None));
    }

    /// <summary>
    /// #964: the Download health screen's connectivity-check card reads its
    /// skip history from Activity rather than a counter of its own, so the
    /// write here (in the remediation pass, not the poll above) is the whole
    /// contract the card depends on.
    /// </summary>
    [Fact]
    public async Task A_skipped_remediation_pass_is_recorded_in_activity_for_the_settings_card_to_read()
    {
        var check = new FakeConnectivityCheck(reachable: false);
        using var world = await World.CreateAsync(check, connectivityCheckEnabled: true);
        var overview = await world.PollAsync();

        var report = await world.RemediateAsync(overview);

        Assert.Contains(report.Notes, note => note.Contains("skipped", StringComparison.OrdinalIgnoreCase));
        var activity = await world.Jobs.ListActivityAsync(10, "download_client", "connectivity-check", CancellationToken.None);
        var entry = Assert.Single(activity);
        Assert.Equal("download.health.connectivity-check-skipped", entry.Category);
        Assert.Contains("no reachable URL", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The crux: a strike already accruing before the outage is frozen, not
    /// wiped, by the skipped poll - a naive "no observation this poll" would
    /// otherwise read as "the problem cleared", which is the opposite of what
    /// an outage guard is for.
    /// </summary>
    [Fact]
    public async Task An_already_accruing_strike_is_frozen_not_cleared_by_a_failed_check()
    {
        var check = new FakeConnectivityCheck(reachable: true);
        using var world = await World.CreateAsync(check, connectivityCheckEnabled: true);
        await world.PollAsync();
        world.Advance(TimeSpan.FromMinutes(31));
        var beforeOutage = await world.PollAsync();
        var strikesBefore = Assert.Single(Assert.Single(beforeOutage.Clients).Queue).HealthFindings!.Single().StrikeCount;
        Assert.True(strikesBefore >= 1);

        check.Reachable = false;
        world.Advance(TimeSpan.FromMinutes(10));
        var duringOutage = await world.PollAsync();
        Assert.Empty(Assert.Single(Assert.Single(duringOutage.Clients).Queue).HealthFindings!);

        check.Reachable = true;
        world.Advance(TimeSpan.FromMinutes(10));
        var afterOutage = await world.PollAsync();
        var strikesAfter = Assert.Single(Assert.Single(afterOutage.Clients).Queue).HealthFindings!.Single().StrikeCount;

        Assert.Equal(strikesBefore, strikesAfter);
    }

    private sealed class StalledClient(string protocol, string clientId, string clientName, Func<DateTimeOffset> addedUtc) : IDownloadClient
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
                    "deluno-movies", DownloadQueueStatuses.Downloading, 42, 0, 0, 4_000_000_000, 1_600_000_000, 0, "Fixture", null,
                    addedUtc())],
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
        private readonly FixedTimeProvider _time;

        private World(TestStorage storage, DownloadClientTelemetryService service, SqliteDownloadHealthRepository healthRepository, SqliteJobStore jobs, FixedTimeProvider time)
        {
            _storage = storage;
            _service = service;
            HealthRepository = healthRepository;
            Jobs = jobs;
            _time = time;
        }

        public SqliteDownloadHealthRepository HealthRepository { get; }

        public SqliteJobStore Jobs { get; }

        public static async Task<World> CreateAsync(FakeConnectivityCheck check, bool connectivityCheckEnabled)
        {
            var storage = TestStorage.Create();
            var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-27T09:00:00Z"));
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            var secretProtection = TestSecretProtection.Create(storage);
            var settingsRepository = new SqlitePlatformSettingsRepository(storage.Factory, time, secretProtection);
            await settingsRepository.SaveAsync(
                PlatformSettingsPatchMerger.Apply(
                    await settingsRepository.GetAsync(CancellationToken.None),
                    new PatchPlatformSettingsRequest(
                        DownloadHealthConnectivityCheckEnabled: connectivityCheckEnabled,
                        DownloadHealthConnectivityCheckUrls: "https://fixture.invalid")),
                CancellationToken.None);
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

            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-dune", Release, "Fixture Indexer",
                client.Id, client.Name, "sent", null, cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(dispatchId, QueueItemId, 4_000_000_000, completed: false, CancellationToken.None);

            var addedUtc = time.GetUtcNow().AddMinutes(-31); // already past the default no-progress window
            var fakeClient = new StalledClient("qbittorrent", client.Id, client.Name, () => addedUtc);
            var registry = new DownloadClientRegistry([fakeClient]);
            var healthRepository = new SqliteDownloadHealthRepository(storage.Factory, time);

            var service = new DownloadClientTelemetryService(
                settingsRepository, healthRepository, libraries, connections,
                jobs, registry, time, resiliencePolicy, jobs, dispatches, jobs,
                new DownloadHealthEvaluator(), processors, NullConnectivityMonitor.Instance,
                connectivityCheck: check);

            return new World(storage, service, healthRepository, jobs, time);
        }

        public void Advance(TimeSpan duration) => _time.Advance(duration);

        public Task<DownloadTelemetryOverview> PollAsync() => _service.GetOverviewAsync(CancellationToken.None);

        public Task<DownloadHealthRemediationReport> RemediateAsync(DownloadTelemetryOverview overview)
            => _service.RunConfiguredHealthRemediationAsync(overview, CancellationToken.None);

        public void Dispose() => _storage.Dispose();
    }
}
