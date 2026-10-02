using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
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
/// #1062: which downloads are Deluno's is decided by asking about the exact
/// ids the client reports (archived dispatches included), not by reading the
/// newest 200 dispatches and hoping the right one is among them. A torrent
/// Deluno sent months ago that is still seeding must stay in Transfers, in the
/// counts and in the sharing rule, however many dispatches came after it.
/// </summary>
public sealed class OwnershipDoesNotDependOnHowManyNewerDispatchesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
    private const string OldSeedingHash = "OLD-SEEDING-HASH";
    private const string OutsideHash = "outside-hash";

    [Fact]
    public async Task An_old_archived_torrent_still_seeding_stays_Delunos_after_hundreds_of_newer_dispatches()
    {
        using var world = await World.CreateAsync(newerDispatches: 260);

        var overview = await world.Service.GetOverviewAsync(CancellationToken.None);

        var client = Assert.Single(overview.Clients);
        // Shown (the id is matched ignoring case); the outside items are not.
        Assert.Contains(client.Queue, item => item.Id == OldSeedingHash.ToLowerInvariant());
        Assert.DoesNotContain(client.Queue, item => item.Id == OutsideHash);
    }

    [Fact]
    public async Task The_screen_and_the_remove_guard_agree()
    {
        using var world = await World.CreateAsync(newerDispatches: 260);

        var overview = await world.Service.GetOverviewAsync(CancellationToken.None);
        var listed = Assert.Single(Assert.Single(overview.Clients).Queue, item => item.Id == OldSeedingHash.ToLowerInvariant());
        var own = await world.Service.CheckOwnershipAsync(world.ClientId, listed.Id, CancellationToken.None);
        var outside = await world.Service.CheckOwnershipAsync(world.ClientId, OutsideHash, CancellationToken.None);

        Assert.True(own.Owned);
        Assert.False(outside.Owned);
    }

    [Fact]
    public async Task A_dispatch_recorded_under_Transmissions_old_number_moves_to_the_hash_only_when_the_name_agrees()
    {
        using var world = await World.CreateAsync();

        var overview = await world.Service.GetOverviewAsync(CancellationToken.None);

        var queue = Assert.Single(overview.Clients).Queue;
        // Number 7 named the same release: now filed under its hash, and Deluno's own.
        Assert.Contains(queue, item => item.Id == "legacy-hash");
        Assert.True((await world.Service.CheckOwnershipAsync(world.ClientId, "legacy-hash", CancellationToken.None)).Owned);
        // Number 9 is now a different torrent: not claimed on the number alone.
        Assert.DoesNotContain(queue, item => item.Id == "other-hash");
        Assert.False((await world.Service.CheckOwnershipAsync(world.ClientId, "other-hash", CancellationToken.None)).Owned);
    }

    /// <summary>
    /// #1062: a session number is only good for the session that handed it out.
    /// An archived dispatch that holds a number is never re-bound to whatever
    /// torrent now carries that number, even when the name looks the same.
    /// </summary>
    [Fact]
    public async Task An_archived_dispatch_holding_a_transmission_number_is_never_moved_to_a_hash()
    {
        using var world = await World.CreateAsync();

        var overview = await world.Service.GetOverviewAsync(CancellationToken.None);

        Assert.DoesNotContain(Assert.Single(overview.Clients).Queue, item => item.Id == "archived-hash");
        Assert.False((await world.Service.CheckOwnershipAsync(world.ClientId, "archived-hash", CancellationToken.None)).Owned);
        var stillNumbered = Assert.Single(await world.Dispatches.FindDispatchesByClientItemIdsAsync(world.ClientId, ["11"], CancellationToken.None));
        Assert.Equal("archived", stillNumbered.Status);
    }

    /// <summary>
    /// #1062: only the first poll after Deluno starts looks for dispatches filed
    /// under numbers. A torrent that shows up carrying a number later is not
    /// claimed on it: by then the numbers may name different torrents.
    /// </summary>
    [Fact]
    public async Task A_number_is_only_upgraded_on_the_first_poll_after_start()
    {
        using var world = await World.CreateAsync();
        await world.Service.GetOverviewAsync(CancellationToken.None);
        world.Client.ShowLateItem = true;

        var second = await world.Service.GetOverviewAsync(CancellationToken.None);

        Assert.DoesNotContain(Assert.Single(second.Clients).Queue, item => item.Id == "late-hash");
        var stillNumbered = Assert.Single(await world.Dispatches.FindDispatchesByClientItemIdsAsync(world.ClientId, ["13"], CancellationToken.None));
        Assert.Equal("13", stillNumbered.TorrentHashOrItemId);
    }

    /// <summary>
    /// #1062: the number tolerates case, punctuation and separators in the name,
    /// and a dispatch's health records (strikes, ignore, progress clock) move
    /// with it to the hash.
    /// </summary>
    [Fact]
    public async Task An_upgraded_dispatch_takes_its_health_records_to_the_hash()
    {
        using var world = await World.CreateAsync();
        await world.Health.RecordDownloadProgressAsync(world.ClientId, [("7", 1024)], CancellationToken.None);
        world.Time.Advance(TimeSpan.FromHours(3));

        await world.Service.GetOverviewAsync(CancellationToken.None);

        var moved = await world.Health.RecordDownloadProgressAsync(world.ClientId, [("legacy-hash", 1024)], CancellationToken.None);
        // The clock it had under the number, not a fresh one started now.
        Assert.True(moved["legacy-hash"] < world.Time.GetUtcNow().AddHours(-2));
    }

    /// <summary>
    /// #1062: NZBGet files an item under a small number it can reuse. An archived
    /// dispatch holding that number is proof only when the client shows the same
    /// release under it now: an additional check, never adoption by name alone.
    /// </summary>
    [Fact]
    public async Task A_number_held_by_an_archived_dispatch_needs_the_same_release_name_to_be_Delunos()
    {
        using var world = await World.CreateAsync();

        // The client shows a different download under number 11 by now.
        world.Client.NumericItemName = "Someone.Elses.Download.2026.1080p.WEB";
        Assert.False((await world.Service.CheckOwnershipAsync(world.ClientId, "11", CancellationToken.None)).Owned);

        // Nothing under that number at all: nothing to say it is the same one.
        world.Client.NumericItemName = null;
        Assert.False((await world.Service.CheckOwnershipAsync(world.ClientId, "11", CancellationToken.None)).Owned);

        // The same release (case, punctuation and separators aside): Deluno's own.
        world.Client.NumericItemName = "archived legacy movie 2026 1080p web.nzb";
        Assert.True((await world.Service.CheckOwnershipAsync(world.ClientId, "11", CancellationToken.None)).Owned);
    }

    [Theory]
    [InlineData("archived", "12", "Some.Movie.2026", "Some Movie 2026", true)]
    [InlineData("archived", "12", "Some.Movie.2026", "Some.Movie.2026.nzb", true)]
    [InlineData("archived", "12", "Some.Movie.2026", "A.Different.Movie.2026", false)]
    [InlineData("archived", "12", "Some.Movie.2026", null, false)]
    [InlineData("sent", "12", "Some.Movie.2026", "A.Different.Movie.2026", true)]
    [InlineData("archived", "0123456789abcdef0123456789abcdef01234567", "Some.Movie.2026", "A.Different.Movie.2026", true)]
    public void An_archived_dispatch_with_a_number_needs_the_name_and_nothing_else_does(
        string status, string id, string dispatchName, string? itemName, bool owned)
    {
        var dispatch = new DownloadDispatchItem(
            Id: "d-1",
            LibraryId: "l-1",
            MediaType: "movies",
            EntityType: "movie",
            EntityId: "m-1",
            ReleaseName: dispatchName,
            IndexerName: "Fixture",
            DownloadClientId: "c-1",
            DownloadClientName: "Client",
            Status: status,
            NotesJson: null,
            CreatedUtc: DateTimeOffset.UnixEpoch,
            GrabStatus: "succeeded",
            GrabAttemptedUtc: DateTimeOffset.UnixEpoch,
            GrabResponseCode: 200,
            GrabMessage: null,
            GrabFailureCode: null,
            GrabResponseJson: null,
            DetectedUtc: null,
            TorrentHashOrItemId: id,
            DownloadedBytes: null,
            ImportStatus: null,
            ImportDetectedUtc: null,
            ImportCompletedUtc: null,
            ImportedFilePath: null,
            ImportFailureCode: null,
            ImportFailureMessage: null,
            CircuitOpenUntilUtc: null,
            NextRetryEligibleUtc: null,
            AttemptCount: 1);

        Assert.Equal(owned, DownloadClientTelemetryService.DispatchProvesItem(dispatch, id, itemName));
    }

    [Fact]
    public async Task Asking_for_ids_returns_archived_dispatches_ignoring_case_beyond_any_window()
    {
        using var world = await World.CreateAsync(newerDispatches: 260);

        var found = await world.Dispatches.FindDispatchesByClientItemIdsAsync(
            world.ClientId, [OldSeedingHash.ToLowerInvariant(), "nothing"], CancellationToken.None);

        var dispatch = Assert.Single(found);
        Assert.Equal("archived", dispatch.Status);
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;

        private World(TestStorage storage, DownloadClientTelemetryService service, SqliteDownloadDispatchesRepository dispatches, string clientId, Client fake, SqliteDownloadHealthRepository health, FixedTimeProvider time)
        {
            _storage = storage;
            Service = service;
            Dispatches = dispatches;
            ClientId = clientId;
            Client = fake;
            Health = health;
            Time = time;
        }

        public Client Client { get; }

        public SqliteDownloadHealthRepository Health { get; }

        public FixedTimeProvider Time { get; }

        public DownloadClientTelemetryService Service { get; }

        public SqliteDownloadDispatchesRepository Dispatches { get; }

        public string ClientId { get; }

        public static async Task<World> CreateAsync(int newerDispatches = 0)
        {
            var storage = TestStorage.Create();
            var time = new FixedTimeProvider(Now.AddDays(-90));
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

            // Sent three months ago, stored in capitals, since finished and archived.
            var old = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-old", "Old.Seeding.Movie.2026.1080p.WEB", "Fixture", client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(old, OldSeedingHash, 1024, completed: true, CancellationToken.None);
            await dispatches.ArchiveDispatchAsync(old, "imported long ago", CancellationToken.None);

            // Recorded under Transmission's numeric id, before Deluno keyed it on the hash.
            await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-legacy", "Legacy.Transmission.Movie.2026.1080p.WEB", "Fixture", client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None, clientExternalId: "7");
            await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-renumbered", "Was.Number.Nine.2026.1080p.WEB", "Fixture", client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None, clientExternalId: "9");

            // An older grab, since archived, that still holds a session number, and one
            // in flight that the client only shows once the numbers have moved on.
            var archivedLegacy = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-archived-legacy", "Archived.Legacy.Movie.2026.1080p.WEB", "Fixture", client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None, clientExternalId: "11");
            await dispatches.ArchiveDispatchAsync(archivedLegacy, "imported long ago", CancellationToken.None);
            await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-late-legacy", "Late.Legacy.Movie.2026.1080p.WEB", "Fixture", client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None, clientExternalId: "13");

            // Hundreds of dispatches on the same client since.
            time.Advance(TimeSpan.FromDays(89));
            for (var index = 0; index < newerDispatches; index++)
            {
                time.Advance(TimeSpan.FromMinutes(1));
                await jobs.RecordDownloadDispatchAsync(
                    library.Id, "movies", "movie", $"movie-{index}", $"Newer.Movie.{index}.2026.1080p.WEB", "Fixture", client.Id, client.Name, "sent", null,
                    cancellationToken: CancellationToken.None,
                    clientExternalId: $"newer-{index}");
            }

            var health = new SqliteDownloadHealthRepository(storage.Factory, time);
            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var problems = new SqliteDispatchProblemLog(storage.Factory, time);
            var processors = new SqliteProcessorRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var resilience = new IntegrationResiliencePolicy(time, storage.Factory, NullLogger<IntegrationResiliencePolicy>.Instance);
            var fake = new Client(client);
            var registry = new DownloadClientRegistry([fake]);

            var service = new DownloadClientTelemetryService(
                settings, health, libraries, connections, jobs, registry, time, resilience,
                jobs, dispatches, jobs, new DownloadHealthEvaluator(), processors,
                Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance, blocklist,
                dispatchProblemLog: problems);

            return new World(storage, service, dispatches, client.Id, fake, health, time);
        }

        public void Dispose() => _storage.Dispose();
    }

    private sealed class Client(DownloadClientItem client) : IDownloadClient
    {
        public bool ShowLateItem { get; set; }

        public string? NumericItemName { get; set; }

        public string Protocol => "qbittorrent";

        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, true, true, "none");

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem requested, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
        {
            DownloadQueueItem Item(string id, string release) => new(
                id, client.Id, client.Name, Protocol, "movies", release, release,
                "deluno-movies", DownloadQueueStatuses.Completed, 1.0, 0, 0, 1024, 1024, 0, "Fixture", null, Now.AddDays(-90),
                SourcePath: $"C:/dl/{release}");

            var queue = new List<DownloadQueueItem>
            {
                Item(OldSeedingHash.ToLowerInvariant(), "Old.Seeding.Movie.2026.1080p.WEB"),
                Item(OutsideHash, "Someone.Elses.Download.2026.1080p.WEB"),
                Item("legacy-hash", "Legacy.Transmission.Movie.2026.1080p.WEB") with { LegacyId = "7", Status = DownloadQueueStatuses.Downloading, Progress = 0.4 },
                Item("other-hash", "Another.Torrent.Entirely.2026.1080p.WEB") with { LegacyId = "9" },
                Item("archived-hash", "ARCHIVED legacy movie 2026 1080p WEB") with { LegacyId = "11" }
            };
            if (NumericItemName is not null)
            {
                queue.Add(Item("11", NumericItemName));
            }

            if (ShowLateItem)
            {
                queue.Add(Item("late-hash", "Late.Legacy.Movie.2026.1080p.WEB") with { LegacyId = "13" });
            }
            return Task.FromResult<DownloadClientTelemetrySnapshot?>(new DownloadClientTelemetrySnapshot(
                client.Id, client.Name, Protocol, null, "healthy", null,
                Capabilities,
                DownloadQueueSummary.Of(queue),
                queue,
                [],
                capturedUtc));
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
