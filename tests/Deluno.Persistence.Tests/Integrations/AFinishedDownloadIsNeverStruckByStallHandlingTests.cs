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
/// #1154: a complete download that waits on a processor hand-off, or sits after
/// a failed one for the person to decide, was struck as "stalled for an hour",
/// blocklisted, removed from the client and its files deleted. Nothing about a
/// finished download can stall; only the person (or the library's "if processing
/// fails" choice) may act on it.
/// </summary>
public sealed class AFinishedDownloadIsNeverStruckByStallHandlingTests
{
    private const string Release = "Oppenheimer.2023.REPACK.1080p.BluRay.DD5.1.x264-PTer";

    [Theory]
    [InlineData(DownloadQueueStatuses.ProcessingFailed, 100d)]
    [InlineData(DownloadQueueStatuses.WaitingForProcessor, 100d)]
    [InlineData(DownloadQueueStatuses.Completed, 100d)]
    [InlineData(DownloadQueueStatuses.Stalled, 100d)]
    public async Task A_finished_download_left_for_hours_is_never_refused_removed_or_purged(string status, double progress)
    {
        using var world = await World.CreateAsync(status, progress, "Processing failed: Weir refused the hand-off (HTTP 400)");
        await world.EnableEveryCleanupSettingAsync();

        // Three hours of polls, a remediation pass after each, with every
        // cleanup switch on: far past any strike window.
        for (var poll = 0; poll < 7; poll++)
        {
            var overview = await world.PollAsync();
            var report = await world.RunRemediationAsync(overview);
            Assert.Equal(0, report.ReleasesRefused);
            Assert.Equal(0, report.ReplacementSearchesQueued);
            Assert.Equal(0, report.ClientEntriesRemoved);
            Assert.Equal(0, report.PayloadsPurged);
            world.Time.Advance(TimeSpan.FromMinutes(31));
        }

        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.True(File.Exists(world.SourcePath), "the finished payload must still be on disk");
        var finalOverview = await world.PollAsync();
        var item = Assert.Single(Assert.Single(finalOverview.Clients).Queue);
        Assert.DoesNotContain(item.HealthFindings ?? [], finding => DownloadHealthStrikes.Counts(finding.Kind));
    }

    [Fact]
    public async Task A_download_that_really_stalled_is_still_struck_so_the_test_above_proves_something()
    {
        using var world = await World.CreateAsync(DownloadQueueStatuses.Stalled, 42d, "Tracker unreachable");
        await world.EnableEveryCleanupSettingAsync();

        var refused = 0;
        for (var poll = 0; poll < 7; poll++)
        {
            var overview = await world.PollAsync();
            var report = await world.RunRemediationAsync(overview);
            refused += report.ReleasesRefused;
            world.Time.Advance(TimeSpan.FromMinutes(31));
        }

        Assert.True(refused > 0);
    }

    /// <summary>
    /// "Ignore for a week" on a stalled warning (Activity, the bell, Transfers): the download is exempt from the health rules
    /// while the days last, and the finding counts again once they have passed.
    /// </summary>
    [Fact]
    public async Task A_stalled_download_whose_warning_was_ignored_is_left_alone_for_those_days_and_struck_again_after_them()
    {
        using var world = await World.CreateAsync(DownloadQueueStatuses.Stalled, 42d, "Tracker unreachable");
        await world.EnableEveryCleanupSettingAsync();
        var first = await world.PollAsync();
        var finding = Assert.Single(Assert.Single(Assert.Single(first.Clients).Queue).HealthFindings!, item => DownloadHealthStrikes.Counts(item.Kind));
        world.Time.Advance(TimeSpan.FromMinutes(31));
        await world.PollAsync();

        var ignored = await world.IgnoreAsync(finding.Kind, 7);
        Assert.NotNull(ignored);
        var refusedWhileIgnored = 0;
        for (var poll = 0; poll < 7; poll++)
        {
            var report = await world.RunRemediationAsync(await world.PollAsync());
            refusedWhileIgnored += report.ReleasesRefused + report.ClientEntriesRemoved + report.PayloadsPurged;
            world.Time.Advance(TimeSpan.FromMinutes(31));
        }

        Assert.Equal(0, refusedWhileIgnored);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.True(File.Exists(world.SourcePath), "an ignored warning never costs the download its files");

        world.Time.Advance(TimeSpan.FromDays(8));
        var refusedAfter = 0;
        for (var poll = 0; poll < 7; poll++)
        {
            var report = await world.RunRemediationAsync(await world.PollAsync());
            refusedAfter += report.ReleasesRefused;
            world.Time.Advance(TimeSpan.FromMinutes(31));
        }

        Assert.True(refusedAfter > 0, "once the ignored days have passed the stalled download is struck again");
    }

    private sealed class FixedStateClient(string protocol, string clientId, string clientName, Func<DownloadQueueItem> queueItemFactory) : IDownloadClient
    {
        public string Protocol => protocol;

        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, true, true, "none");

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => Task.FromResult<DownloadClientTelemetrySnapshot?>(new DownloadClientTelemetrySnapshot(
                clientId, clientName, protocol, "http://127.0.0.1:8080", "healthy", null,
                Capabilities,
                new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
                [queueItemFactory()],
                [],
                capturedUtc));

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
            => Task.FromResult(new DownloadClientActionResult(client.Id, queueItemId, action, true, "ok"));

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
        private readonly SqlitePlatformSettingsRepository _settings;
        private readonly string _sourcePath;

        public string SourcePath => _sourcePath;

        private World(
            TestStorage storage,
            FixedTimeProvider time,
            DownloadClientTelemetryService service,
            SqlitePlatformSettingsRepository settings,
            DownloadClientItem client,
            SqliteJobStore jobs,
            SqliteBlockedReleaseRepository blocklist,
            string sourcePath)
        {
            _storage = storage;
            Time = time;
            _service = service;
            _settings = settings;
            ClientId = client.Id;
            ClientName = client.Name;
            Jobs = jobs;
            Blocklist = blocklist;
            _sourcePath = sourcePath;
        }

        public FixedTimeProvider Time { get; }

        public string ClientId { get; }

        public string ClientName { get; }

        public SqliteJobStore Jobs { get; }

        public SqliteBlockedReleaseRepository Blocklist { get; }

        public static async Task<World> CreateAsync(string status, double progress, string message)
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

            var sourcePath = Path.Combine(storage.DataRoot, "downloads", Release, Release + ".mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            await File.WriteAllTextAsync(sourcePath, "finished payload");

            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var processors = new SqliteProcessorRepository(storage.Factory, time, secretProtection);
            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var resiliencePolicy = new IntegrationResiliencePolicy(time, storage.Factory, NullLogger<IntegrationResiliencePolicy>.Instance);

            // Deluno itself grabbed this release (#933): the Queue only shows
            // downloads Deluno recorded dispatching, matched by the id the
            // client handed back.
            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-opp", Release, "Fixture Indexer",
                client.Id, client.Name, "sent", null, cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(dispatchId, "queue-opp", 4_000_000_000, completed: false, CancellationToken.None);

            var fakeClient = new FixedStateClient("qbittorrent", client.Id, client.Name, () => new DownloadQueueItem(
                "queue-opp", client.Id, client.Name, "qbittorrent", "movies", "Oppenheimer", Release,
                "deluno-movies", status, progress, 0, 0, 4_000_000_000, (long)(4_000_000_000 * progress / 100d), 3, "Fixture", message,
                time.GetUtcNow(), SourcePath: sourcePath, LibraryId: library.Id));
            var registry = new DownloadClientRegistry([fakeClient]);

            var service = new DownloadClientTelemetryService(
                settings, new SqliteDownloadHealthRepository(storage.Factory, time), libraries, connections,
                jobs, registry, time, resiliencePolicy, jobs, dispatches, jobs,
                new DownloadHealthEvaluator(), processors, NullConnectivityMonitor.Instance, blocklist);

            return new World(storage, time, service, settings, client, jobs, blocklist, sourcePath);
        }

        public Task<DownloadHealthRecord?> IgnoreAsync(string kind, int days)
            => new SqliteDownloadHealthRepository(_storage.Factory, Time).IgnoreDownloadHealthFindingAsync(ClientId, "queue-opp", kind, days, CancellationToken.None);

        public Task<DownloadTelemetryOverview> PollAsync() => _service.GetOverviewAsync(CancellationToken.None);

        public Task<DownloadHealthRemediationReport> RunRemediationAsync(DownloadTelemetryOverview overview)
            => _service.RunConfiguredHealthRemediationAsync(overview, CancellationToken.None);

        /// <summary>
        /// Every setting that decides what remediation does to a struck
        /// download, switched on - the strongest configuration #881 must
        /// still leave a setup problem alone under.
        /// </summary>
        public async Task EnableEveryCleanupSettingAsync()
        {
            var current = await _settings.GetAsync(CancellationToken.None);
            await _settings.SaveAsync(
                PlatformSettingsPatchMerger.Apply(
                    current,
                    new PatchPlatformSettingsRequest(
                        CleanupBlockReleaseAfterThreshold: true,
                        CleanupQueueReplacementAfterThreshold: true,
                        CleanupRemoveClientEntryAfterThreshold: true,
                        CleanupPurgePayloadAfterThreshold: true)),
                CancellationToken.None);
        }

        public void Dispose() => _storage.Dispose();
    }
}
