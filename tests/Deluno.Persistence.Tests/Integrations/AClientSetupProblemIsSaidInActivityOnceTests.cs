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
/// #881: the one acceptance line #880 left open. A client-reported local
/// setup problem (qBittorrent's <c>missingFiles</c> and friends) never
/// strikes, blocks, removes or deletes anything - but it must still be said
/// in Activity, in plain words, so a person is not left guessing why Deluno
/// left the download alone. The write happens once, when the spell starts;
/// a client that keeps reporting the same problem on every poll must not
/// repeat it.
/// </summary>
public sealed class AClientSetupProblemIsSaidInActivityOnceTests
{
    private const string Release = "Dune.Part.Two.2024.2160p.WEB-DL-DELUNO";

    [Fact]
    public async Task The_first_poll_says_it_once_in_plain_words()
    {
        using var world = await World.CreateAsync();

        var overview = await world.PollAsync();

        var client = Assert.Single(overview.Clients);
        var item = Assert.Single(client.Queue);
        Assert.Contains(item.HealthFindings!, finding => finding.Kind == DownloadHealthStrikes.ClientSetupProblem);

        var activity = await world.Jobs.ListActivityAsync(20, "download_client", world.ClientId, CancellationToken.None);
        var entry = Assert.Single(activity, entry => entry.Category == "download.client.setup-problem");
        Assert.Contains(world.ClientName, entry.Message, StringComparison.Ordinal);
        Assert.Contains(Release, entry.Message, StringComparison.Ordinal);
        Assert.Contains("left the download alone", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_poll_within_the_strike_window_does_not_repeat_it()
    {
        using var world = await World.CreateAsync();

        await world.PollAsync();
        world.Time.Advance(TimeSpan.FromMinutes(10));
        await world.PollAsync();

        var activity = await world.Jobs.ListActivityAsync(20, "download_client", world.ClientId, CancellationToken.None);
        Assert.Single(activity, entry => entry.Category == "download.client.setup-problem");
    }

    [Fact]
    public async Task It_is_never_blocklisted_or_removed()
    {
        using var world = await World.CreateAsync();

        await world.PollAsync();
        world.Time.Advance(TimeSpan.FromMinutes(10));
        var overview = await world.PollAsync();

        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        var item = Assert.Single(Assert.Single(overview.Clients).Queue);
        Assert.NotNull(item.SourcePath);
        Assert.True(File.Exists(item.SourcePath));
    }

    /// <summary>
    /// #881's other half: not only does the passive poll leave a setup
    /// problem alone, remediation - the pass that actually removes, deletes,
    /// blocks and re-searches struck downloads - must too, with every one of
    /// its settings switched on. <see cref="DownloadHealthStrikes.Counts"/>
    /// deliberately excludes <c>client-setup-problem</c>, so nothing here is
    /// ever "struck" and remediation should never reach it at all.
    /// </summary>
    [Fact]
    public async Task Remediation_with_every_cleanup_setting_on_still_leaves_it_alone()
    {
        using var world = await World.CreateAsync();
        await world.EnableEveryCleanupSettingAsync();

        var overview = await world.PollAsync();
        world.Time.Advance(TimeSpan.FromMinutes(10));
        var report = await world.RunRemediationAsync(overview);

        Assert.Equal(0, report.Evaluated);
        Assert.Equal(0, report.ReleasesRefused);
        Assert.Equal(0, report.ReplacementSearchesQueued);
        Assert.Equal(0, report.ClientEntriesRemoved);
        Assert.Equal(0, report.PayloadsPurged);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));

        var afterRemediation = await world.PollAsync();
        var item = Assert.Single(Assert.Single(afterRemediation.Clients).Queue);
        Assert.Contains(item.HealthFindings!, finding => finding.Kind == DownloadHealthStrikes.ClientSetupProblem);
        Assert.NotNull(item.SourcePath);
        Assert.True(File.Exists(item.SourcePath));
    }

    private sealed class SetupProblemClient(string protocol, string clientId, string clientName, Func<DownloadQueueItem> queueItemFactory) : IDownloadClient
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
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DownloadClientHistoryItem>>([]);

        public Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
            => nativeStatus ?? "unknown";
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;
        private readonly DownloadClientTelemetryService _service;
        private readonly SqlitePlatformSettingsRepository _settings;
        private readonly string _sourcePath;

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

            var sourcePath = Path.Combine(storage.DataRoot, "downloads", Release, Release + ".mkv");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            await File.WriteAllTextAsync(sourcePath, "still downloading");

            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var processors = new SqliteProcessorRepository(storage.Factory, time, secretProtection);
            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var resiliencePolicy = new IntegrationResiliencePolicy(time, storage.Factory, NullLogger<IntegrationResiliencePolicy>.Instance);

            // Deluno itself grabbed this release (#933): the Queue only shows
            // downloads Deluno recorded dispatching, matched by the id the
            // client handed back.
            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-dune", Release, "Fixture Indexer",
                client.Id, client.Name, "sent", null, cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(dispatchId, "queue-dune", 4_000_000_000, completed: false, CancellationToken.None);

            var fakeClient = new SetupProblemClient("qbittorrent", client.Id, client.Name, () => new DownloadQueueItem(
                "queue-dune", client.Id, client.Name, "qbittorrent", "movies", "Dune Part Two", Release,
                "deluno-movies", DownloadQueueStatuses.Stalled, 42, 0, 0, 4_000_000_000, 1_600_000_000, 3, "Fixture", "Missing files",
                time.GetUtcNow(), SourcePath: sourcePath, LibraryId: library.Id, IsSetupProblem: true));
            var registry = new DownloadClientRegistry([fakeClient]);

            var service = new DownloadClientTelemetryService(
                settings, new SqliteDownloadHealthRepository(storage.Factory, time), libraries, connections,
                jobs, registry, time, resiliencePolicy, jobs, dispatches, jobs,
                new DownloadHealthEvaluator(), processors, NullConnectivityMonitor.Instance, blocklist);

            return new World(storage, time, service, settings, client, jobs, blocklist, sourcePath);
        }

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
