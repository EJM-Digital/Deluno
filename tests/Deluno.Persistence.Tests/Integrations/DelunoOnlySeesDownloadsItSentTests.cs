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

    /// <summary>
    /// The one that matters most: another tool's completed job in a shared
    /// SABnzbd/NZBGet history (finished, with a path) must never be picked up
    /// by the import planner and imported into a Deluno library.
    /// </summary>
    [Fact]
    public async Task A_completed_download_in_the_clients_history_that_Deluno_did_not_send_is_never_an_import_candidate()
    {
        using var world = await World.CreateAsync();

        var overview = await world.Service.GetOverviewAsync(CancellationToken.None);
        var candidates = Deluno.Worker.Services.WorkPlanner.GetImportCandidates(overview);

        Assert.DoesNotContain(candidates, item => item.SourcePath is not null && item.SourcePath.Contains("Old.Foreign", StringComparison.Ordinal));
        Assert.DoesNotContain(candidates, item => item.Id is ForeignId or Foreign2Id or "history-foreign");
        // Selective, not simply empty: Deluno's own finished download is still picked up.
        Assert.Contains(candidates, item => item.SourcePath == "C:/Downloads/Own.Movie.2026.1080p.WEB");
    }

    /// <summary>Pause, resume and recheck are guarded like every other verb, and nothing reaches the client.</summary>
    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("recheck")]
    [InlineData("force-start")]
    public async Task No_action_reaches_a_download_Deluno_did_not_send(string action)
    {
        using var world = await World.CreateAsync();

        var result = await world.Service.ExecuteActionAsync(world.ClientId, new DownloadClientActionRequest(action, ForeignId), CancellationToken.None);

        Assert.True(result.Refused);
        Assert.False(result.Succeeded);
        Assert.Equal(0, world.ActionsReachedTheClient);
        // ...while its own download can still be paused.
        var own = await world.Service.ExecuteActionAsync(world.ClientId, new DownloadClientActionRequest("pause", OwnId), CancellationToken.None);
        Assert.True(own.Succeeded);
        Assert.Equal(1, world.ActionsReachedTheClient);
    }

    [Fact]
    public async Task An_old_strike_recorded_for_someone_elses_download_is_not_listed_as_a_health_problem()
    {
        using var world = await World.CreateAsync();
        await world.Health.RecordDownloadHealthObservationsAsync(
            world.ClientId,
            [ForeignId, OwnId],
            [
                new DownloadHealthObservation(world.ClientId, ForeignId, "Someone.Elses.Download", "stalled", "warning", "old", null, null, null, null),
                new DownloadHealthObservation(world.ClientId, OwnId, "Own.Movie.2026.1080p.WEB", "stalled", "warning", "old", null, null, null, null)
            ],
            TimeSpan.FromMinutes(30), TimeSpan.FromHours(24), CancellationToken.None);

        var all = await world.Health.ListDownloadHealthRecordsPageAsync(new PageRequest(50, null), CancellationToken.None);
        Assert.Contains(all.Items, record => record.QueueItemId == ForeignId);

        var listed = await DownloadClientEndpointRouteBuilderExtensions.OwnHealthRecordsAsync(
            all.Items, world.Dispatches, CancellationToken.None);

        Assert.DoesNotContain(listed, record => record.QueueItemId == ForeignId);
        Assert.Contains(listed, record => record.QueueItemId == OwnId);
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
    public async Task The_leftover_scan_lists_only_Delunos_own_and_learns_outside_paths_only_to_protect_their_files()
    {
        using var world = await World.CreateAsync();

        var scan = await world.Service.GetSnapshotsForLeftoverScanAsync(CancellationToken.None);

        var client = Assert.Single(scan);
        // Nothing outside is listed or counted...
        Assert.DoesNotContain(client.Queue, item => item.Id is ForeignId or Foreign2Id);
        Assert.DoesNotContain(client.History, item => item.Id == "history-foreign");
        Assert.Contains(client.Queue, item => item.Id == OwnId);
        // ...but where it keeps its files is known, so they are never swept up.
        Assert.Contains("C:/dl/Someone.Elses.Download.2026.1080p.WEB", client.OutsideSourcePaths);
        Assert.Contains("C:/dl/Added.By.Hand.2026.1080p.WEB", client.OutsideSourcePaths);
        Assert.Contains("C:/Downloads/Old.Foreign.Download.2025.1080p.WEB", client.OutsideSourcePaths);
        Assert.DoesNotContain("C:/dl/Own.Movie.2026.1080p.WEB", client.OutsideSourcePaths);
        // The overview never carries them.
        var overview = Assert.Single((await world.Service.GetOverviewAsync(CancellationToken.None)).Clients);
        Assert.Empty(overview.OutsideSourcePaths);
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
            string vanishedDispatchId,
            Client fakeClient)
        {
            _client = fakeClient;
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

        private readonly Client _client;

        /// <summary>How many actions actually reached the download client.</summary>
        public int ActionsReachedTheClient => _client.Actions;

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
            var fakeClient = new Client(client, includeOwn);
            var registry = new DownloadClientRegistry([fakeClient]);

            var service = new DownloadClientTelemetryService(
                settings, health, libraries, connections, jobs, registry, time, resilience,
                jobs, dispatches, jobs, new DownloadHealthEvaluator(), processors,
                Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance, blocklist,
                dispatchAlertRepository: alerts);

            return new World(storage, service, health, dispatches, settings, alerts, client.Id, vanished, fakeClient);
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
                "deluno-movies", status, 0.4, 1, 60, 1024, 400, 0, "Fixture", null, Now.AddHours(-1),
                SourcePath: $"C:/dl/{release}");
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

        public int Actions { get; private set; }

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem requested, string action, string queueItemId, CancellationToken cancellationToken)
        {
            Actions++;
            return Task.FromResult(new DownloadClientActionResult(requested.Id, queueItemId, action, true, "done"));
        }

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
