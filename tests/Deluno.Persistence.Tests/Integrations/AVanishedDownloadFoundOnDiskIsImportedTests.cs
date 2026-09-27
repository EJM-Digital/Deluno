using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #927 stage 2: a download the client had already finished, but cleared
/// from its own list before Deluno imported it (SABnzbd and NZBGet both do
/// this quickly), is found where the client would have saved it and
/// imported as normal - never asked about, because there is nothing to ask.
/// </summary>
public sealed class AVanishedDownloadFoundOnDiskIsImportedTests
{
    private const string Release = "Sintel.2010.1080p.WEB-DL-DELUNO";

    [Fact]
    public async Task A_finished_download_whose_files_are_still_on_disk_is_queued_for_import_without_asking()
    {
        using var world = await World.CreateAsync();
        world.CreateDownloadFolderEntry(Release);

        var recovered = await world.PollWithOtherItemInQueueAsync();

        var item = Assert.Single(recovered.Queue, item => item.SourcePath != null);
        Assert.Equal(DownloadQueueStatuses.Completed, item.Status);
        Assert.EndsWith(Release, item.SourcePath, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(await world.Alerts.GetOpenAlertsAsync(null, 100, CancellationToken.None));
        var activities = await world.Activity.ListActivityAsync(10, null, null, CancellationToken.None);
        var activity = Assert.Single(activities, activity => activity.Category == "download.vanished.recovered");
        Assert.Contains("importing", activity.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_clients_duplicate_suffix_still_matches_the_release_it_grabbed()
    {
        using var world = await World.CreateAsync();
        world.CreateDownloadFolderEntry($"{Release}.1");

        var recovered = await world.PollWithOtherItemInQueueAsync();

        Assert.Contains(recovered.Queue, item => item.SourcePath != null && item.SourcePath.EndsWith($"{Release}.1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Nothing_on_disk_falls_back_to_the_ordinary_vanished_alert()
    {
        using var world = await World.CreateAsync();
        // No matching folder created this time.

        var recovered = await world.PollWithOtherItemInQueueAsync();

        Assert.DoesNotContain(recovered.Queue, item => item.SourcePath != null);
        var alert = Assert.Single(await world.Alerts.GetOpenAlertsAsync(null, 100, CancellationToken.None));
        Assert.Equal("vanished-downloading", alert.AlertKind);
    }

    [Fact]
    public async Task Still_downloading_when_it_vanished_is_never_looked_for_on_disk()
    {
        using var world = await World.CreateAsync(completed: false);
        world.CreateDownloadFolderEntry(Release);

        var recovered = await world.PollWithOtherItemInQueueAsync();

        Assert.DoesNotContain(recovered.Queue, item => item.SourcePath != null);
        Assert.Single(await world.Alerts.GetOpenAlertsAsync(null, 100, CancellationToken.None));
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;
        private readonly DownloadClientTelemetryService _service;
        private readonly DownloadClientItem _client;
        private readonly LibraryItem _library;

        private World(
            TestStorage storage,
            DateTimeOffset now,
            DownloadClientTelemetryService service,
            DownloadClientItem client,
            LibraryItem library,
            string dispatchId,
            SqliteDownloadDispatchesRepository dispatches,
            SqliteDispatchAlertRepository alerts,
            SqliteJobStore jobs)
        {
            _storage = storage;
            Now = now;
            _service = service;
            _client = client;
            _library = library;
            DispatchId = dispatchId;
            Dispatches = dispatches;
            Alerts = alerts;
            Activity = jobs;
        }

        public DateTimeOffset Now { get; }

        public string DispatchId { get; }

        public SqliteDownloadDispatchesRepository Dispatches { get; }

        public SqliteDispatchAlertRepository Alerts { get; }

        public Deluno.Jobs.Data.IActivityFeedRepository Activity { get; }

        public static async Task<World> CreateAsync(bool completed = true)
        {
            var storage = TestStorage.Create();
            var now = DateTimeOffset.Parse("2026-09-27T04:12:00Z");
            var time = new FixedTimeProvider(now);
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            var settings = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var libraries = new SqliteLibrariesRepository(storage.Factory, time);
            var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
            Directory.CreateDirectory(downloadsPath);
            var library = await libraries.CreateLibraryAsync(new CreateLibraryRequest(
                "Movies", "movies", "library", Path.Combine(storage.DataRoot, "movies"), downloadsPath, null,
                "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
            var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
                "qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-sintel", Release, "Fixture", client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None);
            // The client showed it downloading, then reported it finished -
            // stamping ClientCompletedUtc - before it vanished from the list.
            await dispatches.RecordDetectionAsync(dispatchId, "hash-1", 1_000_000, completed: completed, CancellationToken.None);
            var alerts = new SqliteDispatchAlertRepository(storage.Factory, time);
            var service = new DownloadClientTelemetryService(
                settings, new SqliteDownloadHealthRepository(storage.Factory, time), libraries, connections, null!, null!, time, null!,
                jobs, dispatches, jobs, new Deluno.Recovery.Policies.DownloadHealthEvaluator(), null!, Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance,
                new SqliteBlockedReleaseRepository(storage.Factory, time), dispatchAlertRepository: alerts);
            return new World(storage, now, service, client, library, dispatchId, dispatches, alerts, jobs);
        }

        /// <summary>A folder on disk exactly as a download client would have saved this release.</summary>
        public void CreateDownloadFolderEntry(string name)
            => Directory.CreateDirectory(Path.Combine(_library.DownloadsPath!, name));

        public async Task<DownloadClientTelemetrySnapshot> PollWithOtherItemInQueueAsync()
        {
            var snapshot = new DownloadClientTelemetrySnapshot(
                _client.Id, _client.Name, _client.Protocol, null, "healthy", null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, false, true, "basic"),
                new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
                Queue: [Queued("hash-other")], History: [], Now);
            var clientDispatches = new[] { (await Dispatches.GetDispatchAsync(DispatchId, CancellationToken.None))! };
            return await _service.RecordWhatTheClientShowsAsync(snapshot, clientDispatches, CancellationToken.None);
        }

        private DownloadQueueItem Queued(string id)
            => new(id, _client.Id, _client.Name, "torrent", "movies", "Something Else", "Something.Else.2020.1080p.WEB-DL-DELUNO",
                "movies", DownloadQueueStatuses.Downloading, 50, 0, 0, 1024, 100, 0, "Fixture", null, Now);

        public void Dispose() => _storage.Dispose();
    }
}
