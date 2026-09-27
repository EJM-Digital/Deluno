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
/// #884: SABnzbd's own history has no dedicated disk-full status the way
/// NZBGet's WARNING/SPACE is — every client-side unpack or repair failure,
/// disk-full included, comes back as a bare "Failed" with the reason only in
/// <c>fail_message</c>. Before this, SABnzbd's free-text disk-full wording
/// ("Unpacking failed, disk full", "Unpacking failed, write error or disk is
/// full?", "Repairing failed, Disk full") fell through to the generic
/// client-reported-failure path <see cref="AClientReportedFailureIsBlockedAndSearchedAgainTests"/>
/// covers: blocklisted and searched for again, when the release itself was
/// never at fault. This gives SABnzbd the same parity NZBGet already has.
/// </summary>
public sealed class ASabnzbdDiskFullIsNotTreatedAsAFailedDownloadTests
{
    private const string Release = "Abbott.Elementary.2021.S05E01.720p.HMAX.WEB-DL-FUZEER";

    [Theory]
    [InlineData("Unpacking failed, disk full")]
    [InlineData("Unpacking failed, write error or disk is full?")]
    [InlineData("Repairing failed, Disk full")]
    public async Task Disk_full_is_not_blocklisted(string failMessage)
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(SabHistory(failMessage));

        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Disk_full_does_not_queue_a_replacement_search()
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(SabHistory("Unpacking failed, write error or disk is full?"));

        Assert.Empty(await world.Jobs.ListAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task Disk_full_is_not_recorded_as_a_client_reported_failure()
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(SabHistory("Unpacking failed, write error or disk is full?"));

        var dispatch = await world.Dispatches.GetDispatchAsync(world.DispatchId, CancellationToken.None);
        Assert.NotEqual("failed", dispatch!.ImportStatus);
        Assert.NotEqual(DownloadClientFailureCodes.ClientReported, dispatch.ImportFailureCode);
    }

    /// <summary>A person reading the timeline sees why SABnzbd stopped, in plain words.</summary>
    [Fact]
    public async Task Disk_full_is_recorded_with_a_clear_message()
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(SabHistory("Unpacking failed, write error or disk is full?"));

        var timeline = await world.Dispatches.GetDispatchTimelineAsync(world.DispatchId, CancellationToken.None);
        var entry = Assert.Single(timeline, item => item.EventType == "client-reported-needs-attention");
        Assert.Contains("ran out of disk space", entry.DetailsJson);
        Assert.Contains("Free space", entry.DetailsJson);
    }

    /// <summary>A genuinely bad SABnzbd failure is unaffected: it is still blocklisted and searched for.</summary>
    [Fact]
    public async Task A_non_disk_full_failure_is_still_blocklisted_and_searched_again()
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(SabHistory("Aborted, ran out of articles"));

        Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Single(await world.Jobs.ListAsync(10, CancellationToken.None));
    }

    private static DownloadClientHistoryItem SabHistory(string failMessage)
    {
        var outcome = SabnzbdDownloadClient.NormalizeHistoryOutcome("Failed", failMessage);
        var errorMessage = outcome switch
        {
            DownloadClientHistoryOutcomes.NeedsAttention => SabnzbdDownloadClient.AttentionReason(failMessage),
            DownloadClientHistoryOutcomes.Duplicate => SabnzbdDownloadClient.DuplicateReason(failMessage),
            "failed" => SabnzbdDownloadClient.FailureReason(failMessage),
            _ => null
        };
        return new DownloadClientHistoryItem(
            Id: "331", ClientId: "client-1", ClientName: "SABnzbd", Protocol: "sabnzbd",
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
            SqliteJobStore jobs,
            SqliteDownloadDispatchesRepository dispatches,
            SqliteBlockedReleaseRepository blocklist)
        {
            _storage = storage;
            Now = now;
            _service = service;
            _client = client;
            DispatchId = dispatchId;
            Jobs = jobs;
            Dispatches = dispatches;
            Blocklist = blocklist;
        }

        public DateTimeOffset Now { get; }

        public string DispatchId { get; }

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
                "SABnzbd", "sabnzbd", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "tv", "episode", "episode-abbott-501", Release, "NZBGeek", client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None);
            // The id SABnzbd gave this grab, so history matches it back to the dispatch.
            await dispatches.RecordDetectionAsync(dispatchId, "331", null, completed: false, CancellationToken.None);

            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var service = new DownloadClientTelemetryService(
                settings, new SqliteDownloadHealthRepository(storage.Factory, time), libraries, connections, null!, null!, time, null!,
                jobs, dispatches, jobs, new Deluno.Recovery.Policies.DownloadHealthEvaluator(), null!, Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance, blocklist);
            return new World(storage, now, service, client, dispatchId, jobs, dispatches, blocklist);
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
