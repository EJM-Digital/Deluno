using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #1081: no screen's request waits on a download client. The clients are read
/// by one background refresher into a snapshot that everything else reads, and
/// the orphaned-downloads list is scanned the same way.
/// </summary>
public sealed class DownloadTelemetrySnapshotTests(ITestOutputHelper output)
{
    private const int ClientCount = 5;
    private const int ItemsPerClient = 200;

    [Fact]
    public async Task A_snapshot_is_served_without_calling_the_clients()
    {
        using var world = await World.CreateAsync();
        await world.Store.RefreshAsync(TimeSpan.FromTicks(-1), CancellationToken.None);
        var callsAfterWarmUp = world.Fake.TotalCalls;
        Assert.Equal(ClientCount, callsAfterWarmUp);

        for (var i = 0; i < 20; i++)
        {
            var overview = await world.Service.GetLatestOverviewAsync(TimeSpan.FromHours(1), CancellationToken.None);
            Assert.Equal(ClientCount, overview.Clients.Count);
            Assert.All(overview.Clients, client => Assert.Equal(ItemsPerClient, client.Queue.Count));
        }

        Assert.Equal(callsAfterWarmUp, world.Fake.TotalCalls);
    }

    [Fact]
    public async Task Concurrent_refreshes_share_one_pass_over_the_clients()
    {
        using var world = await World.CreateAsync();
        world.Fake.DelayAll(TimeSpan.FromMilliseconds(150));

        var refreshes = Enumerable.Range(0, 25)
            .Select(_ => world.Store.RefreshAsync(TimeSpan.FromTicks(-1), CancellationToken.None))
            .ToArray();
        await Task.WhenAll(refreshes);

        // One pass = one call per client, however many callers asked at once.
        Assert.Equal(ClientCount, world.Fake.TotalCalls);
        Assert.Single(refreshes.Select(task => task.Result.CapturedUtc).Distinct());
    }

    [Fact]
    public async Task Concurrent_latest_reads_before_the_first_capture_also_share_one_pass()
    {
        using var world = await World.CreateAsync();
        // This is about sharing one pass, not about the 1.5 s cold-wait budget: on a loaded machine (the full gate runs
        // the web tests alongside) the first pass took longer than that, and the readers timed out with an empty answer.
        ((DownloadTelemetrySnapshotStore)world.Store).ColdWait = TimeSpan.FromSeconds(60);
        // Start the first pass, then read while it is in flight: the readers join it rather than start their own.
        var first = world.Store.RefreshAsync(TimeSpan.FromTicks(-1), CancellationToken.None);

        var reads = await Task.WhenAll(Enumerable.Range(0, 25)
            .Select(_ => world.Service.GetLatestOverviewAsync(TimeSpan.FromSeconds(5), CancellationToken.None)));

        await first;
        Assert.All(reads, overview => Assert.Equal(ClientCount, overview.Clients.Count));
        Assert.Equal(ClientCount, world.Fake.TotalCalls);
    }

    [Fact]
    public async Task A_hanging_client_never_holds_up_a_screen_which_gets_the_last_snapshot()
    {
        using var world = await World.CreateAsync();
        await world.Store.RefreshAsync(TimeSpan.FromTicks(-1), CancellationToken.None);
        var firstCapture = world.Store.CapturedUtc!.Value;

        // Every client now hangs until released, so the only way the read can
        // come back is by not waiting for them. That is checked as a condition
        // (it completed while the clients were still held), not as an elapsed
        // time, which only measured how busy the machine was.
        world.Fake.HoldAll();
        try
        {
            // maxAge zero: the reading is "too old", so a refresh is started - and not waited for.
            var overview = await world.Service.GetLatestOverviewAsync(TimeSpan.Zero, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(30));
            AssertSnapshotServed(overview, world, firstCapture);
        }
        finally
        {
            world.Fake.Release();
        }
    }

    private static void AssertSnapshotServed(DownloadTelemetryOverview overview, World world, DateTimeOffset firstCapture)
    {
        Assert.Equal(ClientCount, overview.Clients.Count);
        Assert.All(overview.Clients, client => Assert.Equal(ItemsPerClient, client.Queue.Count));
        Assert.Equal(firstCapture, world.Store.CapturedUtc);
        Assert.True(DateTimeOffset.UtcNow - overview.CapturedUtc >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Clients_are_read_side_by_side_not_one_after_another()
    {
        using var world = await World.CreateAsync();
        // Each client call waits until every client has been called. One
        // after another, the first would wait for the rest forever and the
        // read could never finish, so finishing proves they ran side by side.
        // No elapsed-time bound, which only measured how busy the machine was.
        world.Fake.RequireConcurrentCalls(ClientCount);

        await world.Service.GetOverviewAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(ClientCount, world.Fake.PeakConcurrentCalls);
    }

    [Fact]
    public async Task Reading_a_screen_writes_nothing_and_the_refresher_does_the_writing()
    {
        using var world = await World.CreateAsync();
        await world.Store.RefreshAsync(TimeSpan.FromTicks(-1), CancellationToken.None);
        var writesAfterFirstPass = world.HealthWrites;
        Assert.True(writesAfterFirstPass > 0, "a pass records progress and health observations");

        for (var i = 0; i < 10; i++)
        {
            await world.Service.GetLatestOverviewAsync(TimeSpan.FromHours(1), CancellationToken.None);
        }

        Assert.Equal(writesAfterFirstPass, world.HealthWrites);

        await world.Store.RefreshAsync(TimeSpan.FromTicks(-1), CancellationToken.None);
        Assert.True(world.HealthWrites > writesAfterFirstPass);
    }

    [Fact]
    public async Task One_pass_gives_the_overview_and_the_leftover_scan_and_the_overview_carries_no_outside_paths()
    {
        using var world = await World.CreateAsync();

        var entry = await world.Store.RefreshAsync(TimeSpan.FromTicks(-1), CancellationToken.None);

        Assert.Equal(ClientCount, world.Fake.TotalCalls);
        Assert.All(entry.Value.Overview.Clients, client => Assert.Empty(client.OutsideSourcePaths));
        Assert.Equal(ClientCount, entry.Value.LeftoverSnapshots.Count);
    }

    [Fact]
    public async Task The_orphan_list_is_read_from_memory_and_never_walks_folders_in_the_request()
    {
        using var world = await World.CreateAsync();
        const string Release = "Orphan.Release.2026.1080p.WEB";
        await world.Jobs.RecordDownloadDispatchAsync(
            world.LibraryId, "movies", "movie", "movie-orphan", Release, "Fixture", world.ClientIds[0], "Client 0", "sent", null,
            cancellationToken: CancellationToken.None);
        var first = Path.Combine(world.Downloads, Release);
        Directory.CreateDirectory(first);
        await File.WriteAllTextAsync(Path.Combine(first, "movie.mkv"), "x");
        Directory.SetLastWriteTimeUtc(first, DateTime.UtcNow - TimeSpan.FromHours(5));

        await world.Cache.RefreshAsync(CancellationToken.None);
        var listed = await world.Orphans.ListCachedAsync(CancellationToken.None);
        Assert.Equal(Release, Assert.Single(listed).Name);

        // A second orphan appears on disk. A request must not go and look for it.
        const string Second = "Orphan.Release.Two.2026.1080p.WEB";
        await world.Jobs.RecordDownloadDispatchAsync(
            world.LibraryId, "movies", "movie", "movie-orphan-2", Second, "Fixture", world.ClientIds[0], "Client 0", "sent", null,
            cancellationToken: CancellationToken.None);
        var second = Path.Combine(world.Downloads, Second);
        Directory.CreateDirectory(second);
        Directory.SetLastWriteTimeUtc(second, DateTime.UtcNow - TimeSpan.FromHours(5));
        var callsBefore = world.Fake.TotalCalls;

        var again = await world.Orphans.ListCachedAsync(CancellationToken.None);

        // Served from the cache means no client was asked; the elapsed time
        // of a read is only a measure of how busy the machine is.
        Assert.Single(again);
        Assert.Equal(callsBefore, world.Fake.TotalCalls);

        // The background scan finds it.
        await world.Cache.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, (await world.Orphans.ListCachedAsync(CancellationToken.None)).Count);

        // Acting on one updates the list at once, without waiting for the next scan.
        var kept = await world.Orphans.KeepAsync(first, CancellationToken.None);
        Assert.True(kept.Done, kept.Message);
        Assert.Equal(Second, Assert.Single(await world.Orphans.ListCachedAsync(CancellationToken.None)).Name);
    }

    [Fact]
    public async Task Timing_for_five_clients_of_two_hundred_items_before_and_after()
    {
        using var world = await World.CreateAsync();
        // A real qBittorrent answers a 200-torrent list in a few hundred milliseconds.
        world.Fake.DelayAll(TimeSpan.FromMilliseconds(300));

        var live = Stopwatch.StartNew();
        await world.Service.GetOverviewAsync(CancellationToken.None);
        live.Stop();
        var liveOrphans = Stopwatch.StartNew();
        await world.Orphans.ListAsync(CancellationToken.None);
        liveOrphans.Stop();

        await world.Store.RefreshAsync(TimeSpan.FromTicks(-1), CancellationToken.None);
        await world.Cache.RefreshAsync(CancellationToken.None);
        var callsBeforeServed = world.Fake.TotalCalls;
        var served = Stopwatch.StartNew();
        await world.Service.GetLatestOverviewAsync(DownloadTelemetryFreshness.Screen, CancellationToken.None);
        served.Stop();
        var servedOrphans = Stopwatch.StartNew();
        await world.Orphans.ListCachedAsync(CancellationToken.None);
        servedOrphans.Stop();

        output.WriteLine($"GET telemetry: live pass {live.ElapsedMilliseconds} ms, served from the snapshot {served.ElapsedMilliseconds} ms");
        output.WriteLine($"GET orphans:   live pass {liveOrphans.ElapsedMilliseconds} ms, served from the cache {servedOrphans.ElapsedMilliseconds} ms");
        // Served from the snapshot and the cache means no client was asked.
        // The timings above are printed for the record, not asserted: an
        // elapsed-time bound only measured how busy the machine was.
        Assert.Equal(callsBeforeServed, world.Fake.TotalCalls);
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage storage;

        private World(TestStorage storage)
        {
            this.storage = storage;
        }

        public FakeClients Fake { get; private set; } = null!;

        public DownloadClientTelemetryService Service { get; private set; } = null!;

        public IDownloadTelemetrySnapshotStore Store { get; private set; } = null!;

        public OrphanedDownloadCache Cache { get; private set; } = null!;

        public OrphanedDownloadService Orphans { get; private set; } = null!;

        public SqliteJobStore Jobs { get; private set; } = null!;

        public string LibraryId { get; private set; } = string.Empty;

        public string Downloads { get; private set; } = string.Empty;

        public IReadOnlyList<string> ClientIds { get; private set; } = [];

        private CountingHealth healthProxy = null!;

        public int HealthWrites => healthProxy.Writes;

        public static async Task<World> CreateAsync()
        {
            var storage = TestStorage.Create();
            var world = new World(storage);
            var time = TimeProvider.System;
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

            var settings = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var libraries = new SqliteLibrariesRepository(storage.Factory, time);
            world.Downloads = Path.Combine(storage.DataRoot, "downloads");
            Directory.CreateDirectory(world.Downloads);
            var library = await libraries.CreateLibraryAsync(new CreateLibraryRequest(
                "Movies", "movies", "library", Path.Combine(storage.DataRoot, "movies"), world.Downloads, null,
                "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
            world.LibraryId = library.Id;

            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            world.Jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var ids = new List<string>();
            for (var index = 0; index < ClientCount; index++)
            {
                var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
                    $"Client {index}", "qbittorrent", "localhost", 8080 + index, null, null, null, "deluno-movies", "deluno-tv", null, index + 1, true), CancellationToken.None);
                await connections.UpdateDownloadClientHealthAsync(client.Id, "healthy", "Connected.", null, 5, CancellationToken.None);
                ids.Add(client.Id);
                // Deluno only sees what it sent (#1062), so each item is a dispatch the client handed an id back for.
                for (var item = 0; item < ItemsPerClient; item++)
                {
                    var dispatch = await world.Jobs.RecordDownloadDispatchAsync(
                        library.Id, "movies", "movie", $"movie-{index}-{item}", $"Release.{item}", "Fixture", client.Id, client.Name, "sent", null,
                        cancellationToken: CancellationToken.None);
                    await dispatches.RecordDetectionAsync(dispatch, $"{client.Id}-{item}", 1024, completed: false, CancellationToken.None);
                }

            }

            world.ClientIds = ids;
            var health = new SqliteDownloadHealthRepository(storage.Factory, time);
            world.healthProxy = CountingHealth.Wrap(health);
            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var problems = new SqliteDispatchProblemLog(storage.Factory, time);
            var processors = new SqliteProcessorRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var resilience = new IntegrationResiliencePolicy(time, storage.Factory, NullLogger<IntegrationResiliencePolicy>.Instance);
            world.Fake = new FakeClients();
            var registry = new DownloadClientRegistry([world.Fake]);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(time);
            services.AddSingleton<DownloadTelemetrySnapshotStore>();
            services.AddSingleton<IDownloadTelemetrySnapshotStore>(sp => sp.GetRequiredService<DownloadTelemetrySnapshotStore>());
            services.AddSingleton<OrphanedDownloadCache>();
            services.AddScoped<IDownloadClientTelemetryService>(sp => new DownloadClientTelemetryService(
                settings, world.healthProxy.Proxy, libraries, connections, world.Jobs, registry, time, resilience,
                world.Jobs, dispatches, world.Jobs, new DownloadHealthEvaluator(), processors,
                Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance, blocklist,
                dispatchProblemLog: problems,
                snapshotStore: sp.GetRequiredService<IDownloadTelemetrySnapshotStore>()));
            services.AddScoped<IOrphanedDownloadService>(sp => world.BuildOrphans(
                sp.GetRequiredService<IDownloadClientTelemetryService>(), settings, libraries, processors,
                sp.GetRequiredService<OrphanedDownloadCache>(), storage));
            var provider = services.BuildServiceProvider();
            world.provider = provider;
            world.Store = provider.GetRequiredService<IDownloadTelemetrySnapshotStore>();
            world.Cache = provider.GetRequiredService<OrphanedDownloadCache>();
            world.Service = (DownloadClientTelemetryService)provider.CreateScope().ServiceProvider.GetRequiredService<IDownloadClientTelemetryService>();
            world.Orphans = (OrphanedDownloadService)provider.CreateScope().ServiceProvider.GetRequiredService<IOrphanedDownloadService>();
            return world;
        }

        private ServiceProvider provider = null!;

        private OrphanedDownloadService BuildOrphans(
            IDownloadClientTelemetryService telemetry,
            SqlitePlatformSettingsRepository settings,
            SqliteLibrariesRepository libraries,
            SqliteProcessorRepository processors,
            OrphanedDownloadCache cache,
            TestStorage testStorage)
            => new(
                libraries,
                settings,
                telemetry,
                Jobs,
                processors,
                new SqliteKeptDownloadsRepository(testStorage.Factory, TimeProvider.System),
                Jobs,
                TimeProvider.System,
                NullLogger<OrphanedDownloadService>.Instance,
                cache: cache);

        public void Dispose()
        {
            provider.Dispose();
            storage.Dispose();
        }
    }

    /// <summary>Counts every call that writes (Record*, Upsert*, Clear*, Save*, Resolve*, Purge*) on the health repository.</summary>
    public class CountingHealth : DispatchProxy
    {
        private IDownloadHealthRepository target = null!;
        private int writes;

        public int Writes => Volatile.Read(ref writes);

        public IDownloadHealthRepository Proxy => (IDownloadHealthRepository)(object)this;

        public static CountingHealth Wrap(IDownloadHealthRepository real)
        {
            var proxy = Create<IDownloadHealthRepository, CountingHealth>();
            var counting = (CountingHealth)(object)proxy;
            counting.target = real;
            return counting;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            if (name.StartsWith("Record", StringComparison.Ordinal)
                || name.StartsWith("Upsert", StringComparison.Ordinal)
                || name.StartsWith("Clear", StringComparison.Ordinal)
                || name.StartsWith("Save", StringComparison.Ordinal)
                || name.StartsWith("Resolve", StringComparison.Ordinal)
                || name.StartsWith("Purge", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref writes);
            }

            try
            {
                return targetMethod.Invoke(target, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                throw exception.InnerException;
            }
        }
    }

    private sealed class FakeClients : IDownloadClient
    {
        private readonly ConcurrentDictionary<string, int> calls = new();
        private TimeSpan delay = TimeSpan.Zero;

        public string Protocol => "qbittorrent";

        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, true, true, "none");

        public int TotalCalls => calls.Values.Sum();

        public void DelayAll(TimeSpan value) => delay = value;

        private TaskCompletionSource? hold;
        private int requiredConcurrent;
        private int concurrent;
        private int peakConcurrent;
        private TaskCompletionSource allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PeakConcurrentCalls => Volatile.Read(ref peakConcurrent);

        /// <summary>Every call waits until <see cref="Release"/>.</summary>
        public void HoldAll() => hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => hold?.TrySetResult();

        /// <summary>Every call waits until this many calls are in flight at once.</summary>
        public void RequireConcurrentCalls(int count) => requiredConcurrent = count;

        public async Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem requested, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
        {
            calls.AddOrUpdate(requested.Id, 1, (_, count) => count + 1);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken); // reason: a fake client that takes this long to answer, so the polling under test sees a slow client
            }

            if (hold is not null)
            {
                await hold.Task.WaitAsync(cancellationToken);
            }

            if (requiredConcurrent > 0)
            {
                var now = Interlocked.Increment(ref concurrent);
                int seen;
                while ((seen = Volatile.Read(ref peakConcurrent)) < now
                    && Interlocked.CompareExchange(ref peakConcurrent, now, seen) != seen) { }
                if (now >= requiredConcurrent)
                {
                    allArrived.TrySetResult();
                }

                await allArrived.Task.WaitAsync(cancellationToken);
            }

            var queue = Enumerable.Range(0, ItemsPerClient).Select(index => new DownloadQueueItem(
                $"{requested.Id}-{index}", requested.Id, requested.Name, Protocol, "movies", $"Release.{index}", $"Release.{index}",
                "deluno-movies", DownloadQueueStatuses.Downloading, 0.4, 1, 60, 1024, 400, 0, "Fixture", null, capturedUtc.AddHours(-1),
                SourcePath: $"C:/dl/{requested.Id}/Release.{index}")).ToArray();
            return new DownloadClientTelemetrySnapshot(
                requested.Id, requested.Name, Protocol, null, "healthy", null,
                Capabilities, DownloadQueueSummary.Of(queue), queue, [], capturedUtc);
        }

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem requested, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem requested, string action, string queueItemId, CancellationToken cancellationToken)
            => Task.FromResult(new DownloadClientActionResult(requested.Id, queueItemId, action, true, "done"));

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
