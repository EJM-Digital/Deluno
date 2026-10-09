using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #1192: the same NZB went to SABnzbd and to NZBGet. NZBGet's copy failed (the folder name was taken)
/// and Deluno blocked the release and deleted the copy's files, though SABnzbd's copy was fine. A
/// failure on an extra copy never blocks the release, queues a search or deletes anything while another
/// send of that release is downloading or has imported.
/// </summary>
public sealed class AFailedDuplicateSendNeverBlocksTheReleaseTests
{
    private const string Release = "Big.Buck.Bunny.2008.1080p.WEB-DL.x264-GOLDEN";

    [Fact]
    public async Task A_duplicate_failing_while_the_first_send_downloads_blocks_nothing_and_deletes_nothing()
    {
        using var world = await World.CreateAsync(removeFailed: true);
        await world.SendFirstToSabnzbdAsync();

        await world.ShowNzbGetFailureAsync();

        var duplicate = await world.Dispatches.GetDispatchAsync(world.DuplicateId, CancellationToken.None);
        Assert.Equal("failed", duplicate!.ImportStatus);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Empty(await world.Store.ListAsync(10, CancellationToken.None));
        Assert.Null(world.NzbGet.LastAction);
        var first = await world.Dispatches.GetDispatchAsync(world.FirstId, CancellationToken.None);
        Assert.Null(first!.ImportStatus);

        var note = Assert.Single(await world.Store.ListActivityAsync(20, null, null, CancellationToken.None), item => item.Category == "download.duplicate.failed");
        Assert.Contains("still downloading in SABnzbd", note.Message);
        Assert.Contains("deleted nothing", note.Message);
    }

    [Fact]
    public async Task A_duplicate_failing_after_the_first_send_imported_blocks_nothing_and_deletes_nothing()
    {
        using var world = await World.CreateAsync(removeFailed: true);
        await world.SendFirstToSabnzbdAsync();
        await world.Dispatches.RecordImportOutcomeAsync(world.FirstId, "imported", "C:/Media/Movies/Big Buck Bunny.mkv", null, null, CancellationToken.None);

        await world.ShowNzbGetFailureAsync();

        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Empty(await world.Store.ListAsync(10, CancellationToken.None));
        Assert.Null(world.NzbGet.LastAction);
        var note = Assert.Single(await world.Store.ListActivityAsync(20, null, null, CancellationToken.None), item => item.Category == "download.duplicate.failed");
        Assert.Contains("already imported from SABnzbd", note.Message);
    }

    [Fact]
    public async Task A_release_that_failed_everywhere_is_still_blocked()
    {
        using var world = await World.CreateAsync(removeFailed: false);
        await world.SendFirstToSabnzbdAsync();
        await world.Dispatches.RecordImportOutcomeAsync(world.FirstId, "failed", null, DownloadClientFailureCodes.ClientReported, "SABnzbd could not download it.", CancellationToken.None);

        await world.ShowNzbGetFailureAsync();

        Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Single(await world.Store.ListAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task A_failure_with_no_other_send_is_blocked_as_before()
    {
        using var world = await World.CreateAsync(removeFailed: false);

        await world.ShowNzbGetFailureAsync();

        Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Single(await world.Store.ListAsync(10, CancellationToken.None));
    }

    private sealed class FakeClient(string protocol) : IDownloadClient
    {
        public string Protocol => protocol;

        public string? LastAction { get; private set; }

        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, true, true, protocol);

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
        {
            LastAction = action;
            return Task.FromResult(new DownloadClientActionResult(client.Id, queueItemId, action, true, "done"));
        }

        public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

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
        private readonly DownloadClientItem _nzbget;
        private readonly DownloadClientItem _sab;
        private readonly string _libraryId;
        private readonly DateTimeOffset _now;

        private World(
            TestStorage storage, DateTimeOffset now, DownloadClientTelemetryService service, DownloadClientItem nzbget,
            DownloadClientItem sab, string libraryId, string duplicateId, SqliteJobStore store,
            SqliteDownloadDispatchesRepository dispatches, SqliteBlockedReleaseRepository blocklist, FakeClient nzbGetClient)
        {
            _storage = storage;
            _now = now;
            _service = service;
            _nzbget = nzbget;
            _sab = sab;
            _libraryId = libraryId;
            DuplicateId = duplicateId;
            Store = store;
            Dispatches = dispatches;
            Blocklist = blocklist;
            NzbGet = nzbGetClient;
        }

        public string DuplicateId { get; }

        public string FirstId { get; private set; } = string.Empty;

        public SqliteJobStore Store { get; }

        public SqliteDownloadDispatchesRepository Dispatches { get; }

        public SqliteBlockedReleaseRepository Blocklist { get; }

        public FakeClient NzbGet { get; }

        public static async Task<World> CreateAsync(bool removeFailed)
        {
            var storage = TestStorage.Create();
            var now = DateTimeOffset.Parse("2026-10-09T02:47:00Z");
            var time = new FixedTimeProvider(now);
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            var settings = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var libraries = new SqliteLibrariesRepository(storage.Factory, time);
            var library = await libraries.CreateLibraryAsync(new CreateLibraryRequest(
                "Movies", "movies", "library", Path.Combine(storage.DataRoot, "movies"), Path.Combine(storage.DataRoot, "downloads"), null,
                "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
            var nzbget = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
                "NZBGet", "nzbget", "localhost", 6789, null, null, null, "movies", "tv", null, 1, true, RemoveFailed: removeFailed), CancellationToken.None);
            var sab = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
                "SABnzbd", "sabnzbd", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var store = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var duplicateId = await store.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-bbb", Release, "Golden Newznab stub", nzbget.Id, nzbget.Name, "sent", null,
                cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(duplicateId, "331", null, completed: false, CancellationToken.None);
            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var fake = new FakeClient("nzbget");
            var service = new DownloadClientTelemetryService(
                settings, new SqliteDownloadHealthRepository(storage.Factory, time), libraries, connections, null!,
                new DownloadClientRegistry([fake]), time, null!,
                store, dispatches, store, new Deluno.Recovery.Policies.DownloadHealthEvaluator(), null!, Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance, blocklist);
            return new World(storage, now, service, nzbget, sab, library.Id, duplicateId, store, dispatches, blocklist, fake);
        }

        /// <summary>The first send of the release, to SABnzbd, as the add-time search made it.</summary>
        public async Task SendFirstToSabnzbdAsync()
        {
            FirstId = await Store.RecordDownloadDispatchAsync(
                _libraryId, "movies", "movie", "movie-bbb", Release, "Golden Newznab stub", _sab.Id, _sab.Name, "sent", null,
                cancellationToken: CancellationToken.None);
            await Dispatches.RecordDetectionAsync(FirstId, "SAB_1", null, completed: false, CancellationToken.None);
        }

        public async Task ShowNzbGetFailureAsync()
        {
            var history = new DownloadClientHistoryItem(
                Id: "331", ClientId: _nzbget.Id, ClientName: _nzbget.Name, Protocol: "nzbget",
                MediaType: "movies", Title: "Big Buck Bunny", ReleaseName: Release, Category: "movies",
                Outcome: NzbGetDownloadClient.NormalizeOutcome("FAILURE/MOVE"), IndexerName: "Golden Newznab stub", SizeBytes: 1_697_526_155,
                CompletedUtc: _now, ErrorMessage: NzbGetDownloadClient.FailureReason("FAILURE/MOVE"),
                HistorySource: "native", ExternalId: "331");
            var snapshot = new DownloadClientTelemetrySnapshot(
                _nzbget.Id, _nzbget.Name, _nzbget.Protocol, null, "healthy", null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, false, true, "basic"),
                new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
                Queue: [],
                History: [history],
                _now);
            var dispatch = (await Dispatches.GetDispatchAsync(DuplicateId, CancellationToken.None))!;
            await _service.RecordWhatTheClientShowsAsync(snapshot, [dispatch], CancellationToken.None);
        }

        public void Dispose() => _storage.Dispose();
    }
}
