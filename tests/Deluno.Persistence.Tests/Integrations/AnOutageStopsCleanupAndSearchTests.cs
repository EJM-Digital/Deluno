using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Contracts.Connectivity;
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
/// #901: while the internet is down, nothing that judges a download - refuse
/// it, remove it from the client, purge its payload, or queue a replacement
/// search - may act, because none of it can currently tell a bad release
/// apart from every release simply being unable to reach anything.
/// <see cref="Integrations.AStoppedDownloadIsRefusedOnTheBlocklistTests"/>
/// covers the same pass with the internet up.
/// </summary>
public sealed class AnOutageStopsCleanupAndSearchTests
{
    private const string Release = "Arrival.2016.1080p.WEB";

    [Fact]
    public async Task A_download_past_the_strike_threshold_is_left_alone_during_an_outage()
    {
        using var world = await World.CreateAsync(isOutage: true);

        var report = await world.RemediateAsync(DownloadHealthStrikes.ClientStalled, strikes: 5);

        Assert.Equal(0, report.Evaluated);
        Assert.Equal(0, report.ReleasesRefused);
        Assert.Equal(0, report.ReplacementSearchesQueued);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        // Every job queued past the fixture's own dispatch would be a
        // replacement search - none is queued while the outage lasts.
        Assert.Empty(await world.Jobs.ListAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task The_same_download_is_refused_and_searched_for_once_the_outage_ends()
    {
        using var world = await World.CreateAsync(isOutage: true);
        await world.RemediateAsync(DownloadHealthStrikes.ClientStalled, strikes: 5);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));

        world.Connectivity.IsOutage = false;
        var report = await world.RemediateAsync(DownloadHealthStrikes.ClientStalled, strikes: 5);

        Assert.Equal(1, report.ReleasesRefused);
        Assert.Equal(1, report.ReplacementSearchesQueued);
        Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
    }

    private static DownloadHealthFinding Finding(string kind, int strikes)
        => new("critical", kind, "Stopped", "Queue status: stalled.", "Review", false, false, StrikeCount: strikes);

    private sealed class FakeConnectivityMonitor : IConnectivityMonitor
    {
        public bool IsOutage { get; set; }

        public Task<ConnectivityStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Current);

        public ConnectivityStatus Current => new(
            IsOutage,
            IsOutage ? DateTimeOffset.Parse("2026-09-22T09:00:00Z") : null,
            DateTimeOffset.Parse("2026-09-22T10:00:00Z"),
            ["1.1.1.1:443"],
            IsOutage ? "Internet down since 09:00 — cleanup and searching paused." : "Connected.");

        public Task<TimeSpan> OutageOverlapAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
            => Task.FromResult(TimeSpan.Zero);
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;
        private readonly FixedTimeProvider _time;
        private readonly DownloadClientTelemetryService _service;
        private readonly DownloadClientItem _client;

        public FakeConnectivityMonitor Connectivity { get; }

        private World(TestStorage storage, FixedTimeProvider time, DownloadClientTelemetryService service, DownloadClientItem client, SqliteJobStore jobs, SqliteBlockedReleaseRepository blocklist, FakeConnectivityMonitor connectivity)
        {
            _storage = storage;
            _time = time;
            _service = service;
            _client = client;
            Jobs = jobs;
            Blocklist = blocklist;
            Connectivity = connectivity;
        }

        public SqliteJobStore Jobs { get; }

        public SqliteBlockedReleaseRepository Blocklist { get; }

        public static async Task<World> CreateAsync(bool isOutage)
        {
            var storage = TestStorage.Create();
            var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-22T10:00:00Z"));
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
            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-arrival", Release, "Fixture", client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(dispatchId, "queue-arrival", 1024, completed: false, CancellationToken.None);
            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var connectivity = new FakeConnectivityMonitor { IsOutage = isOutage };
            var service = new DownloadClientTelemetryService(
                settings, new SqliteDownloadHealthRepository(storage.Factory, time), libraries, connections, null!, null!, time, null!,
                jobs, dispatches, jobs, new DownloadHealthEvaluator(), null!, connectivity, blocklist);
            return new World(storage, time, service, client, jobs, blocklist, connectivity);
        }

        public Task<DownloadHealthRemediationReport> RemediateAsync(string kind, int strikes)
        {
            var item = new DownloadQueueItem("queue-arrival", _client.Id, _client.Name, _client.Protocol, "movies", "Arrival", Release,
                "deluno-movies", DownloadQueueStatuses.Stalled, 15, 0, 0, 1024, 128, 0, "Fixture", null, _time.GetUtcNow(), HealthFindings: [Finding(kind, strikes)]);
            var snapshot = new DownloadClientTelemetrySnapshot(_client.Id, _client.Name, _client.Protocol, null, "healthy", null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "none"),
                new DownloadTelemetrySummary(0, 0, 0, 1, 0, 0, 0), [item], [], _time.GetUtcNow());
            return _service.RunConfiguredHealthRemediationAsync(
                new DownloadTelemetryOverview(snapshot.Summary, [snapshot], _time.GetUtcNow()), CancellationToken.None);
        }

        public void Dispose() => _storage.Dispose();
    }
}
