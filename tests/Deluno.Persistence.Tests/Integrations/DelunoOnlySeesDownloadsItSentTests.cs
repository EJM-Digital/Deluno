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
/// #1062 (James, 30 Sep 2026): Deluno only ever sees and acts on downloads it
/// sent. Anything added to a download client outside Deluno is invisible to
/// it: not listed, counted, cleaned or touched, on any surface that reads the
/// client's queue or history.
///
/// <para>One client reports two downloads Deluno sent (one by its recorded id,
/// one still waiting for its id and recognised by name), two nobody at Deluno
/// sent, and the same again in its own history. Everything downstream is
/// driven through the real <c>GetOverviewAsync</c> pipeline.</para>
/// </summary>
public sealed class DelunoOnlySeesDownloadsItSentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
    private const string OwnId = "own-hash";
    private const string PendingId = "pending-hash";
    private const string PendingRelease = "Pending.Movie.2026.1080p.WEB";
    private const string ForeignId = "foreign-hash";
    private const string Foreign2Id = "foreign-hash-2";

    [Fact]
    public async Task The_overview_lists_and_counts_only_what_Deluno_sent()
    {
        using var world = await World.CreateAsync();

        var overview = await world.Service.GetOverviewAsync(CancellationToken.None);

        var client = Assert.Single(overview.Clients);
        Assert.Equal(new[] { OwnId, PendingId }.Order(StringComparer.Ordinal).ToArray(), client.Queue.Select(item => item.Id).Order(StringComparer.Ordinal).ToArray());
        // Counted: two of Deluno's own are downloading; the two outside ones are not in any number.
        Assert.Equal(2, client.Summary.ActiveCount);
        Assert.Equal(2, overview.Summary.ActiveCount);
        Assert.Equal(0, client.Summary.StalledCount);
    }

    [Fact]
    public async Task The_clients_own_history_shows_only_what_Deluno_sent()
    {
        using var world = await World.CreateAsync();

        var overview = await world.Service.GetOverviewAsync(CancellationToken.None);

        var client = Assert.Single(overview.Clients);
        Assert.Contains(client.History, item => item.Id == "history-own");
        Assert.DoesNotContain(client.History, item => item.Id == "history-foreign");
    }

    [Fact]
    public async Task An_outside_item_gets_no_strike()
    {
        using var world = await World.CreateAsync();

        await world.Service.GetOverviewAsync(CancellationToken.None);

        var records = await world.Health.ListDownloadHealthRecordsPageAsync(new PageRequest(50, null), CancellationToken.None);
        Assert.DoesNotContain(records.Items, record => record.QueueItemId is ForeignId or Foreign2Id);
    }

    [Fact]
    public async Task A_download_recognised_by_name_while_waiting_for_its_id_is_given_the_id_so_it_stays_Delunos()
    {
        using var world = await World.CreateAsync();

        await world.Service.GetOverviewAsync(CancellationToken.None);

        var adopted = await world.Dispatches.FindAnyDispatchByHashAsync(world.ClientId, PendingId, CancellationToken.None);
        Assert.NotNull(adopted);
        Assert.Equal(PendingRelease, adopted!.ReleaseName);
    }

    [Fact]
    public async Task The_leftover_scan_alone_still_reads_outside_items_so_their_files_are_never_swept_up()
    {
        using var world = await World.CreateAsync();

        var everything = await world.Service.GetUnfilteredSnapshotsAsync(CancellationToken.None);

        var client = Assert.Single(everything);
        Assert.Contains(client.Queue, item => item.Id == ForeignId);
        Assert.Contains(client.Queue, item => item.Id == OwnId);
        Assert.Contains(client.History, item => item.Id == "history-foreign");
        // The same pass never struck them either.
        var records = await world.Health.ListDownloadHealthRecordsPageAsync(new PageRequest(50, null), CancellationToken.None);
        Assert.DoesNotContain(records.Items, record => record.QueueItemId is ForeignId or Foreign2Id);
    }

    [Fact]
    public async Task Outside_items_do_not_count_towards_pausing_automatic_searching()
    {
        using var world = await World.CreateAsync();
        var settings = await world.Settings.GetAsync(CancellationToken.None);
        await world.Settings.SaveAsync(
            PlatformSettingsPatchMerger.Apply(settings, new PatchPlatformSettingsRequest(PauseSearchWhenQueueLongEnabled: true, PauseSearchWhenQueueLongThreshold: 2)),
            CancellationToken.None);
        var gate = new SearchQueuePauseGate(world.Settings, world.Service, new SearchQueuePauseMemory());

        // Two of Deluno's own are waiting (not over 2); four items are in the client.
        var decision = await gate.EvaluateAsync("movies", CancellationToken.None);

        Assert.Equal(2, decision.Waiting);
        Assert.False(decision.Paused);
    }

    [Fact]
    public async Task A_client_showing_only_outside_items_does_not_hide_that_a_download_of_Delunos_vanished()
    {
        using var world = await World.CreateAsync(includeOwn: false);

        await world.Service.GetOverviewAsync(CancellationToken.None);

        // The dispatch Deluno had seen at "vanished-hash" is nowhere in the client's queue or history,
        // which still lists other people's downloads: that is a vanished download, not an empty answer.
        Assert.True(await world.Alerts.HasUnresolvedAlertAsync(world.VanishedDispatchId, "vanished-downloading", CancellationToken.None));
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;

        private World(
            TestStorage storage,
            DownloadClientTelemetryService service,
            SqliteDownloadHealthRepository health,
            SqliteDownloadDispatchesRepository dispatches,
            SqlitePlatformSettingsRepository settings,
            SqliteDispatchAlertRepository alerts,
            string clientId,
            string vanishedDispatchId)
        {
            _storage = storage;
            Service = service;
            Health = health;
            Dispatches = dispatches;
            Settings = settings;
            Alerts = alerts;
            ClientId = clientId;
            VanishedDispatchId = vanishedDispatchId;
        }

        public DownloadClientTelemetryService Service { get; }

        public SqliteDownloadHealthRepository Health { get; }

        public SqliteDownloadDispatchesRepository Dispatches { get; }

        public SqlitePlatformSettingsRepository Settings { get; }

        public SqliteDispatchAlertRepository Alerts { get; }

        public string ClientId { get; }

        public string VanishedDispatchId { get; }

        public static async Task<World> CreateAsync(bool includeOwn = true)
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

            // Sent, and the client handed its id back.
            var ownDispatch = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-own", "Own.Movie.2026.1080p.WEB", "Fixture", client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(ownDispatch, OwnId, 1024, completed: false, CancellationToken.None);
            // Sent a minute ago; the client has not handed an id back yet.
            await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-pending", PendingRelease, "Fixture", client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None);
            // Seen before, now nowhere in the client's answer.
            var vanished = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-vanished", "Vanished.Movie.2026.1080p.WEB", "Fixture", client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(vanished, "vanished-hash", 1024, completed: false, CancellationToken.None);
            // The history row Deluno derived from its own record of the first download.
            var health = new SqliteDownloadHealthRepository(storage.Factory, time);
            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var alerts = new SqliteDispatchAlertRepository(storage.Factory, time);
            var processors = new SqliteProcessorRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var resilience = new IntegrationResiliencePolicy(time, storage.Factory, NullLogger<IntegrationResiliencePolicy>.Instance);
            var registry = new DownloadClientRegistry([new Client(client, includeOwn)]);

            var service = new DownloadClientTelemetryService(
                settings, health, libraries, connections, jobs, registry, time, resilience,
                jobs, dispatches, jobs, new DownloadHealthEvaluator(), processors,
                Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance, blocklist,
                dispatchAlertRepository: alerts);

            return new World(storage, service, health, dispatches, settings, alerts, client.Id, vanished);
        }

        public void Dispose() => _storage.Dispose();
    }

    private sealed class Client(DownloadClientItem client, bool includeOwn) : IDownloadClient
    {
        public string Protocol => "qbittorrent";

        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, true, true, "none");

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem requested, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
        {
            DownloadQueueItem Item(string id, string release, string status) => new(
                id, client.Id, client.Name, Protocol, "movies", release, release,
                "deluno-movies", status, 0.4, 1, 60, 1024, 400, 0, "Fixture", null, Now.AddHours(-1));
            DownloadClientHistoryItem History(string id, string release) => new(
                id, client.Id, client.Name, Protocol, "movies", release, release, "deluno-movies", "completed", "Fixture",
                1024, Now.AddMinutes(-30), null, SourcePath: $"C:/Downloads/{release}", HistorySource: "native", ExternalId: id);

            var queue = new List<DownloadQueueItem>
            {
                Item(ForeignId, "Someone.Elses.Download.2026.1080p.WEB", DownloadQueueStatuses.Downloading),
                Item(Foreign2Id, "Added.By.Hand.2026.1080p.WEB", DownloadQueueStatuses.Stalled)
            };
            var history = new List<DownloadClientHistoryItem> { History("history-foreign", "Old.Foreign.Download.2025.1080p.WEB") };
            if (includeOwn)
            {
                queue.Add(Item(OwnId, "Own.Movie.2026.1080p.WEB", DownloadQueueStatuses.Downloading));
                queue.Add(Item(PendingId, PendingRelease, DownloadQueueStatuses.Downloading));
                history.Add(History(OwnId + "-done", "Own.Movie.2026.1080p.WEB") with { Id = "history-own", ExternalId = OwnId });
            }

            return Task.FromResult<DownloadClientTelemetrySnapshot?>(new DownloadClientTelemetrySnapshot(
                client.Id, client.Name, Protocol, null, "healthy", null,
                Capabilities,
                DownloadQueueSummary.Of(queue),
                queue,
                history,
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
