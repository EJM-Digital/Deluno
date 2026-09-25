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
/// #663: NZBGet's own duplicate check correctly declining a release it
/// already held — DELETED/DUPE, DELETED/COPY, DELETED/GOOD — used to be read
/// exactly like <see cref="AClientReportedFailureIsBlockedAndSearchedAgainTests"/>'s
/// real failures: filed as "failed", blocklisted, and searched for again. Seen
/// live: "NZBGet deleted it: it was a duplicate of one NZBGet already held."
/// read as a failure and the release was blocklisted over NZBGet working
/// correctly.
/// </summary>
public sealed class ANzbGetDuplicateIsNotTreatedAsAFailedDownloadTests
{
    private const string Release = "Abbott.Elementary.2021.S05E01.720p.HMAX.WEB-DL-FUZEER";

    [Theory]
    [InlineData("DELETED/DUPE")]
    [InlineData("DELETED/COPY")]
    [InlineData("DELETED/GOOD")]
    public async Task A_duplicate_is_not_blocklisted(string status)
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(NzbGetHistory(status));

        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("DELETED/DUPE")]
    [InlineData("DELETED/COPY")]
    [InlineData("DELETED/GOOD")]
    public async Task A_duplicate_does_not_queue_a_replacement_search(string status)
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(NzbGetHistory(status));

        Assert.Empty(await world.Jobs.ListAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task A_duplicate_is_not_recorded_as_a_client_reported_failure()
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(NzbGetHistory("DELETED/DUPE"));

        var dispatch = await world.Dispatches.GetDispatchAsync(world.DispatchId, CancellationToken.None);
        Assert.NotEqual("failed", dispatch!.ImportStatus);
        Assert.NotEqual(DownloadClientFailureCodes.ClientReported, dispatch.ImportFailureCode);
    }

    /// <summary>The dispatch stops looking like it is still in flight, whatever else it becomes.</summary>
    [Fact]
    public async Task A_duplicate_still_marks_the_dispatch_detected_and_finished()
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(NzbGetHistory("DELETED/DUPE"));

        var dispatch = await world.Dispatches.GetDispatchAsync(world.DispatchId, CancellationToken.None);
        Assert.NotNull(dispatch!.DetectedUtc);
        Assert.NotNull(dispatch.ClientCompletedUtc);
    }

    /// <summary>
    /// "Follow the existing item": another dispatch for the same episode has
    /// already imported, so this duplicate is resolved as satisfied by it
    /// rather than left unresolved.
    /// </summary>
    [Fact]
    public async Task A_duplicate_follows_an_already_imported_dispatch_for_the_same_title()
    {
        using var world = await World.CreateAsync();
        await world.Dispatches.RecordImportOutcomeAsync(
            world.EarlierImportedDispatchId, "imported", @"C:\tv\Abbott Elementary\S05E01.mkv", null, null, CancellationToken.None);

        await world.ShowHistoryAsync(NzbGetHistory("DELETED/DUPE"));

        var dispatch = await world.Dispatches.GetDispatchAsync(world.DispatchId, CancellationToken.None);
        Assert.Equal("imported", dispatch!.ImportStatus);
        Assert.Equal(@"C:\tv\Abbott Elementary\S05E01.mkv", dispatch.ImportedFilePath);
    }

    [Fact]
    public async Task A_duplicate_already_recorded_is_not_reprocessed_on_the_next_poll()
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(NzbGetHistory("DELETED/DUPE"));
        await world.ShowHistoryAsync(NzbGetHistory("DELETED/DUPE"));

        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        var timeline = await world.Dispatches.GetDispatchTimelineAsync(world.DispatchId, CancellationToken.None);
        Assert.Single(timeline, entry => entry.EventType == "client-reported-duplicate");
    }

    private static DownloadClientHistoryItem NzbGetHistory(string status)
    {
        var outcome = NzbGetDownloadClient.NormalizeOutcome(status);
        var errorMessage = outcome == DownloadClientHistoryOutcomes.Duplicate ? NzbGetDownloadClient.DuplicateReason(status) : null;
        return new DownloadClientHistoryItem(
            Id: "331", ClientId: "client-1", ClientName: "NZBGet", Protocol: "nzbget",
            MediaType: "tv", Title: "Abbott Elementary", ReleaseName: Release, Category: "tv",
            Outcome: outcome, IndexerName: "NZBGeek", SizeBytes: 2_000_000_000,
            CompletedUtc: DateTimeOffset.Parse("2026-09-25T03:42:00Z"), ErrorMessage: errorMessage,
            HistorySource: "native", ExternalId: "331");
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;
        private readonly DownloadClientTelemetryService _service;
        private readonly DownloadClientItem _client;

        private World(
            TestStorage storage,
            DateTimeOffset now,
            DownloadClientTelemetryService service,
            DownloadClientItem client,
            string dispatchId,
            string earlierImportedDispatchId,
            SqliteJobStore jobs,
            SqliteDownloadDispatchesRepository dispatches,
            SqliteBlockedReleaseRepository blocklist)
        {
            _storage = storage;
            Now = now;
            _service = service;
            _client = client;
            DispatchId = dispatchId;
            EarlierImportedDispatchId = earlierImportedDispatchId;
            Jobs = jobs;
            Dispatches = dispatches;
            Blocklist = blocklist;
        }

        public DateTimeOffset Now { get; }

        public string DispatchId { get; }

        /// <summary>A second dispatch for the same episode, for the "follow the existing item" test.</summary>
        public string EarlierImportedDispatchId { get; }

        public SqliteJobStore Jobs { get; }

        public SqliteDownloadDispatchesRepository Dispatches { get; }

        public SqliteBlockedReleaseRepository Blocklist { get; }

        public static async Task<World> CreateAsync()
        {
            var storage = TestStorage.Create();
            var now = DateTimeOffset.Parse("2026-09-25T04:12:00Z");
            var time = new FixedTimeProvider(now);
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            var settings = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var libraries = new SqliteLibrariesRepository(storage.Factory, time);
            var library = await libraries.CreateLibraryAsync(new CreateLibraryRequest(
                "TV", "tv", "library", Path.Combine(storage.DataRoot, "tv"), Path.Combine(storage.DataRoot, "downloads"), null,
                "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
            var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
                "NZBGet", "nzbget", "localhost", 6789, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "tv", "episode", "episode-abbott-501", Release, "NZBGeek", client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None);
            // The id NZBGet gave this grab, so history matches it back to the dispatch.
            await dispatches.RecordDetectionAsync(dispatchId, "331", null, completed: false, CancellationToken.None);

            // An earlier attempt at the same episode, already imported —
            // what a duplicate should be able to follow.
            var earlierImportedDispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "tv", "episode", "episode-abbott-501", Release, "NZBGeek", client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None);

            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var service = new DownloadClientTelemetryService(
                settings, new SqliteDownloadHealthRepository(storage.Factory, time), libraries, connections, null!, null!, time, null!,
                jobs, dispatches, jobs, new Deluno.Recovery.Policies.DownloadHealthEvaluator(), null!, blocklist);
            return new World(storage, now, service, client, dispatchId, earlierImportedDispatchId, jobs, dispatches, blocklist);
        }

        public Task ShowHistoryAsync(DownloadClientHistoryItem history)
        {
            var snapshot = new DownloadClientTelemetrySnapshot(
                _client.Id, _client.Name, _client.Protocol, null, "healthy", null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, false, true, "basic"),
                new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
                Queue: [],
                History: [history with { ClientId = _client.Id, ClientName = _client.Name }],
                Now);
            return RecordAsync(snapshot);
        }

        public async Task RecordAsync(DownloadClientTelemetrySnapshot snapshot)
        {
            var clientDispatches = new[] { (await Dispatches.GetDispatchAsync(DispatchId, CancellationToken.None))! };
            await _service.RecordWhatTheClientShowsAsync(snapshot, clientDispatches, CancellationToken.None);
        }

        public void Dispose() => _storage.Dispose();
    }
}
