using Deluno.Connections.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Contracts.Connectivity;
using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Recovery.Policies;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #1491: each step in a download's life reaches Activity, live, once. On the rig between 04:36 and 04:48 qBittorrent
/// moved queued films into active slots and a film picked up a strike, and none of it reached Activity because the
/// strike was only logged. Each test polls the way the rig's clock ran and reads what Activity then says.
/// </summary>
public sealed class EachStepInADownloadsLifeReachesActivityTests
{
    private const string Release = "RoboCop 1987 RM4K 1080p BluRay x264";

    [Fact]
    public async Task A_download_that_moves_from_the_clients_queue_into_an_active_slot_says_so_once()
    {
        using var world = await World.CreateAsync(DownloadQueueStatuses.Queued, 0d);

        await world.PollAsync();
        world.Time.Advance(TimeSpan.FromMinutes(1));
        await world.PollAsync();
        world.Item = world.Item with { Status = DownloadQueueStatuses.Downloading, Progress = 3d, SpeedMbps = 2.5 };
        await world.PollAsync();
        await world.PollAsync();

        var lines = await world.LinesAsync("download.client.queued", "download.client.started");
        Assert.Equal(2, lines.Count);
        Assert.Contains("waiting in the queue of qBittorrent", lines[0].Message);
        Assert.Contains(Release, lines[0].Message);
        Assert.Contains("started downloading in qBittorrent", lines[1].Message);
    }

    [Fact]
    public async Task A_download_the_client_puts_back_in_its_queue_says_so()
    {
        using var world = await World.CreateAsync(DownloadQueueStatuses.Downloading, 10d);
        await world.PollAsync();
        world.Item = world.Item with { Status = DownloadQueueStatuses.Queued, SpeedMbps = 0 };
        await world.PollAsync();
        await world.PollAsync();

        var lines = await world.LinesAsync("download.client.queued");
        Assert.Contains("waiting in the queue of qBittorrent", Assert.Single(lines).Message);
    }

    [Fact]
    public async Task A_finished_download_says_so_once()
    {
        using var world = await World.CreateAsync(DownloadQueueStatuses.Downloading, 80d);
        await world.PollAsync();
        world.Item = world.Item with { Status = DownloadQueueStatuses.ImportReady, Progress = 100d, SpeedMbps = 0 };
        await world.PollAsync();
        await world.PollAsync();

        var line = Assert.Single(await world.LinesAsync("download.client.finished"));
        Assert.Contains("finished downloading in qBittorrent", line.Message);
    }

    [Fact]
    public async Task Each_strike_is_one_line_with_its_rule_number_and_reason_and_a_repeat_poll_adds_none()
    {
        using var world = await World.CreateAsync(DownloadQueueStatuses.Stalled, 42d, "Tracker unreachable");

        await world.PollAsync();
        await world.PollAsync();
        world.Time.Advance(TimeSpan.FromMinutes(31));
        await world.PollAsync();
        await world.PollAsync();

        var lines = await world.LinesAsync("download.health.strike");
        Assert.Equal(2, lines.Count);
        Assert.Contains("strike 1 of 3", lines[0].Message);
        Assert.Contains("strike 2 of 3", lines[1].Message);
        Assert.All(lines, line => Assert.Contains(Release, line.Message));
        Assert.All(lines, line => Assert.Contains("Tracker unreachable", line.Message));
    }

    [Fact]
    public async Task A_download_that_recovers_says_its_strikes_are_cleared_once()
    {
        using var world = await World.CreateAsync(DownloadQueueStatuses.Stalled, 42d, "Tracker unreachable");
        await world.PollAsync();
        world.Time.Advance(TimeSpan.FromMinutes(31));
        world.Item = world.Item with { Status = DownloadQueueStatuses.Downloading, Progress = 50d, DownloadedBytes = 2_000_000_000, SpeedMbps = 3.2, ErrorMessage = null };
        await world.PollAsync();
        await world.PollAsync();

        var line = Assert.Single(await world.LinesAsync("download.health.strikes-cleared"));
        // #1504: the instant speed is not recovery evidence; a client-reported failure says only that it no longer applies.
        Assert.Contains("no longer applies, strikes cleared", line.Message);
        Assert.DoesNotContain("MB/s", line.Message);
    }

    [Fact]
    public async Task Every_one_of_these_is_pushed_as_it_is_written()
    {
        using var world = await World.CreateAsync(DownloadQueueStatuses.Queued, 0d);
        await world.PollAsync();
        world.Item = world.Item with { Status = DownloadQueueStatuses.Stalled, Progress = 5d, ErrorMessage = "Tracker unreachable" };
        await world.PollAsync();
        world.Item = world.Item with { Status = DownloadQueueStatuses.Downloading, SpeedMbps = 3.2, ErrorMessage = null };
        await world.PollAsync();
        world.Item = world.Item with { Status = DownloadQueueStatuses.ImportReady, Progress = 100d, SpeedMbps = 0 };
        await world.PollAsync();

        var pushed = world.Realtime.ActivityEvents.Select(item => item.Category).ToArray();
        Assert.Contains("download.client.queued", pushed);
        Assert.Contains("download.health.strike", pushed);
        Assert.Contains("download.health.strikes-cleared", pushed);
        Assert.Contains("download.client.finished", pushed);
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

        private World(TestStorage storage, FixedTimeProvider time, DownloadClientTelemetryService service, SqliteJobStore jobs, RecordingRealtimeEventPublisher realtime, DownloadQueueItem item)
        {
            _storage = storage;
            Time = time;
            _service = service;
            Jobs = jobs;
            Realtime = realtime;
            Item = item;
        }

        public FixedTimeProvider Time { get; }

        public SqliteJobStore Jobs { get; }

        public RecordingRealtimeEventPublisher Realtime { get; }

        public DownloadQueueItem Item { get; set; }

        public static async Task<World> CreateAsync(string status, double progress, string? message = null)
        {
            var storage = TestStorage.Create();
            var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-11-05T04:36:00Z"));
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

            var realtime = new RecordingRealtimeEventPublisher();
            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, realtime, dispatches);
            var processors = new SqliteProcessorRepository(storage.Factory, time, secretProtection);
            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var resiliencePolicy = new IntegrationResiliencePolicy(time, storage.Factory, NullLogger<IntegrationResiliencePolicy>.Instance);
            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-robocop", Release, "Fixture Indexer",
                client.Id, client.Name, "sent", null, cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(dispatchId, "queue-robocop", 4_000_000_000, completed: false, CancellationToken.None);

            var item = new DownloadQueueItem(
                "queue-robocop", client.Id, client.Name, "qbittorrent", "movies", "RoboCop", Release,
                "deluno-movies", status, progress, 0, 0, 4_000_000_000, (long)(4_000_000_000 * progress / 100d), 3, "Fixture", message,
                time.GetUtcNow(), LibraryId: library.Id);
            World? self = null;
            var fakeClient = new FixedStateClient("qbittorrent", client.Id, client.Name, () => self!.Item);
            var service = new DownloadClientTelemetryService(
                settings, new SqliteDownloadHealthRepository(storage.Factory, time), libraries, connections,
                jobs, new DownloadClientRegistry([fakeClient]), time, resiliencePolicy, jobs, dispatches, jobs,
                new DownloadHealthEvaluator(), processors, NullConnectivityMonitor.Instance, blocklist);
            self = new World(storage, time, service, jobs, realtime, item);
            return self;
        }

        public Task<DownloadTelemetryOverview> PollAsync() => _service.GetOverviewAsync(CancellationToken.None);

        /// <summary>The lines of these categories, oldest first.</summary>
        public async Task<IReadOnlyList<ActivityEventItem>> LinesAsync(params string[] categories)
            => (await Jobs.ListActivityAsync(200, null, null, CancellationToken.None))
                .Where(item => categories.Contains(item.Category))
                .Reverse()
                .ToArray();

        public void Dispose() => _storage.Dispose();
    }
}
