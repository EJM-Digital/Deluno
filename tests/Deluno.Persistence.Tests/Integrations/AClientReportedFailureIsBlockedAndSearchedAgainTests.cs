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
/// #647: a download NZBGet's own history says failed — FAILURE/HEALTH,
/// FAILURE/PAR, a DELETED status NZBGet did not ask for, a WARNING that is
/// not a script complaint — used to be read exactly like a finished download:
/// the dispatch was marked detected and completed and nothing else happened.
/// It sat on "sent" until a generic detection timeout fired hours later, with
/// no reason, no blocklist entry and no replacement search.
///
/// <para>SABnzbd's own "Failed" history status already reached
/// <see cref="DownloadClientTelemetryService.ShownByClient"/> in the same
/// shape (native, "failed"), so the fix lives once in the shared path both
/// clients funnel through, not per client.</para>
/// </summary>
public sealed class AClientReportedFailureIsBlockedAndSearchedAgainTests
{
    private const string Release = "Abbott.Elementary.2021.S05E01.720p.HMAX.WEB-DL-FUZEER";

    [Fact]
    public async Task NZBGets_FAILURE_HEALTH_ends_the_dispatch_with_NZBGets_reason_in_plain_words()
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(NzbGetHistory("FAILURE/HEALTH"));

        var dispatch = await world.Dispatches.GetDispatchAsync(world.DispatchId, CancellationToken.None);
        Assert.Equal("failed", dispatch!.ImportStatus);
        Assert.Equal(DownloadClientFailureCodes.ClientReported, dispatch.ImportFailureCode);
        Assert.Equal("NZBGet could not download it: too many articles are missing.", dispatch.ImportFailureMessage);
        Assert.NotNull(dispatch.ClientCompletedUtc);
    }

    [Theory]
    [InlineData("FAILURE/PAR", "NZBGet could not download it: it could not repair the download; too much data is missing or damaged.")]
    [InlineData("FAILURE/UNPACK", "NZBGet could not download it: unpacking the download failed.")]
    [InlineData("FAILURE/MOVE", "NZBGet could not download it: it could not move the finished files into place.")]
    [InlineData("WARNING/HEALTH", "NZBGet finished it with a warning: too many articles are missing.")]
    [InlineData("DELETED/HEALTH", "NZBGet deleted it: a health check failed.")]
    [InlineData("DELETED/MANUAL", "NZBGet deleted it: something asked NZBGet to remove it.")]
    public async Task Every_NZBGet_failure_status_ends_the_dispatch_with_its_own_reason(string status, string reason)
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(NzbGetHistory(status));

        var dispatch = await world.Dispatches.GetDispatchAsync(world.DispatchId, CancellationToken.None);
        Assert.Equal("failed", dispatch!.ImportStatus);
        Assert.Equal(reason, dispatch.ImportFailureMessage);
    }

    [Fact]
    public async Task A_failed_download_is_blocked_the_same_way_a_stalled_torrent_is()
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(NzbGetHistory("FAILURE/HEALTH"));

        var refused = Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Equal(ImportFailurePolicy.ClientReportedFailure, refused.ReasonCode);
        Assert.Equal(BlockedReleaseStates.Refused, refused.State);
        Assert.Equal("episode-abbott-501", refused.EntityId);
        Assert.Equal("NZBGet could not download it: too many articles are missing.", refused.Reason);
        Assert.Contains(BlockedReleaseKeys.For(Release, "NZBGeek"), await world.Blocklist.ListKeysAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_download_queues_a_replacement_search_without_anyone_clicking()
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(NzbGetHistory("FAILURE/HEALTH"));

        Assert.Single(await world.Jobs.ListAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task A_failure_already_recorded_is_not_blocked_or_searched_twice()
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(NzbGetHistory("FAILURE/HEALTH"));
        await world.ShowHistoryAsync(NzbGetHistory("FAILURE/HEALTH"));

        Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Single(await world.Jobs.ListAsync(10, CancellationToken.None));
    }

    /// <summary>
    /// NZBGet's own DELETED/MANUAL is what it writes when something asked it
    /// to remove the item — Deluno's own health-remediation removal included.
    /// That removal already records the dispatch's outcome before NZBGet's
    /// history catches up, so the failure this history row would otherwise
    /// report is not recorded, blocked or searched for a second time: there
    /// is no reason to re-litigate a release Deluno already dealt with.
    /// </summary>
    [Fact]
    public async Task A_deletion_Deluno_already_recorded_an_outcome_for_is_not_treated_as_a_second_failure()
    {
        using var world = await World.CreateAsync();

        await world.Dispatches.RecordImportOutcomeAsync(
            world.DispatchId, "failed", null, "health-remediation-applied", "release refused", CancellationToken.None);

        await world.ShowHistoryAsync(NzbGetHistory("DELETED/MANUAL"));

        var dispatch = await world.Dispatches.GetDispatchAsync(world.DispatchId, CancellationToken.None);
        Assert.Equal("health-remediation-applied", dispatch!.ImportFailureCode);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
    }

    /// <summary>SABnzbd's own "Failed" history status follows the same path as NZBGet's.</summary>
    [Fact]
    public async Task SABnzbds_own_Failed_history_status_follows_the_same_path()
    {
        using var world = await World.CreateAsync();

        var history = new DownloadClientHistoryItem(
            Id: "sab-1", ClientId: world.ClientId, ClientName: "SABnzbd", Protocol: "sabnzbd",
            MediaType: "tv", Title: "Abbott Elementary", ReleaseName: Release, Category: "tv",
            Outcome: "failed", IndexerName: "NZBGeek", SizeBytes: 2_000_000_000,
            CompletedUtc: world.Now, ErrorMessage: "Unpacking failed, write error or disk is full?",
            HistorySource: "native", ExternalId: "331");

        await world.ShowHistoryAsync(history);

        var dispatch = await world.Dispatches.GetDispatchAsync(world.DispatchId, CancellationToken.None);
        Assert.Equal("failed", dispatch!.ImportStatus);
        Assert.Equal("Unpacking failed, write error or disk is full?", dispatch.ImportFailureMessage);
        Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Single(await world.Jobs.ListAsync(10, CancellationToken.None));
    }

    /// <summary>
    /// A history item is found by the id the client gave the grab, whether it
    /// currently sits in the queue or has already moved to history (#647).
    /// </summary>
    [Fact]
    public async Task An_item_that_only_sits_in_history_not_the_queue_is_still_matched_to_its_dispatch()
    {
        using var world = await World.CreateAsync();

        var snapshot = new DownloadClientTelemetrySnapshot(
            world.ClientId, "NZBGet", "nzbget", null, "healthy", null,
            new DownloadClientTelemetryCapabilities(true, true, true, true, false, true, "basic"),
            new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0),
            Queue: [],
            History: [NzbGetHistory("FAILURE/HEALTH")],
            world.Now);

        await world.RecordAsync(snapshot);

        var dispatch = await world.Dispatches.GetDispatchAsync(world.DispatchId, CancellationToken.None);
        Assert.Equal("failed", dispatch!.ImportStatus);
    }

    private static DownloadClientHistoryItem NzbGetHistory(string status)
    {
        var outcome = NzbGetDownloadClient.NormalizeOutcome(status);
        var errorMessage = outcome == "failed" ? NzbGetDownloadClient.FailureReason(status) : null;
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

        public string ClientId => _client.Id;

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
            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var service = new DownloadClientTelemetryService(
                settings, new SqliteDownloadHealthRepository(storage.Factory, time), libraries, connections, null!, null!, time, null!,
                jobs, dispatches, jobs, new Deluno.Recovery.Policies.DownloadHealthEvaluator(), null!, blocklist);
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
