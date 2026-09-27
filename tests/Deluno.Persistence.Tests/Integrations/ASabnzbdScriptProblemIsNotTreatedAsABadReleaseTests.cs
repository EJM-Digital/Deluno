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
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #910: SABnzbd's post-processing script failing hard used to be read exactly
/// like <see cref="AClientReportedFailureIsBlockedAndSearchedAgainTests"/>'s
/// real failures — blocklisted and searched again — over a script the user
/// configured, not the release. A soft script warning used to be silently
/// read as a plain success, which hid it entirely. Neither blocklists or
/// searches now, mirroring NZBGet's
/// <see cref="ANzbGetScriptProblemIsNotTreatedAsABadReleaseTests"/>; both leave
/// the dispatch to the ordinary import path, which probes the finished file.
/// </summary>
public sealed class ASabnzbdScriptProblemIsNotTreatedAsABadReleaseTests
{
    private const string Release = "Abbott.Elementary.2021.S05E01.720p.HMAX.WEB-DL-FUZEER";

    [Fact]
    public async Task A_script_failure_is_not_blocklisted_or_searched_again()
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(SabHistory("Failed", "Script was aborted, please check script_log for more information.", "CustomProcess.py"));

        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Empty(await world.Jobs.ListAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task A_script_warning_is_not_blocklisted_or_searched_again()
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(SabHistory("Completed", "Script exit code 1, notify.sh reported a problem", "notify.sh"));

        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Empty(await world.Jobs.ListAsync(10, CancellationToken.None));
    }

    [Theory]
    [InlineData("Failed", "Script was aborted, please check script_log for more information.")]
    [InlineData("Completed", "Script exit code 1, notify.sh reported a problem")]
    public async Task A_script_problem_leaves_no_import_outcome_so_the_ordinary_import_path_still_probes_it(string status, string failMessage)
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(SabHistory(status, failMessage, "CustomProcess.py"));

        var dispatch = await world.Dispatches.GetDispatchAsync(world.DispatchId, CancellationToken.None);
        Assert.NotEqual("failed", dispatch!.ImportStatus);
        Assert.Null(dispatch.ImportStatus);
        Assert.NotNull(dispatch.ClientCompletedUtc);
    }

    /// <summary>A script failure is critical enough to show on System &gt; Health; a warning is lower severity but still shown.</summary>
    [Fact]
    public async Task A_script_failure_is_recorded_as_a_health_finding_naming_the_script()
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(SabHistory("Failed", "Script was aborted, please check script_log for more information.", "CustomProcess.py"));

        var records = await world.Health.ListDownloadHealthRecordsAsync(10, CancellationToken.None);
        var record = Assert.Single(records, r => r.Kind == DownloadHealthStrikes.ClientScriptFailed);
        Assert.Equal("critical", record.Severity);
        Assert.Contains("CustomProcess.py", record.Evidence);
    }

    [Fact]
    public async Task A_script_warning_is_recorded_at_a_lower_severity_than_a_failure()
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(SabHistory("Completed", "Script exit code 1, notify.sh reported a problem", "notify.sh"));

        var records = await world.Health.ListDownloadHealthRecordsAsync(10, CancellationToken.None);
        var record = Assert.Single(records, r => r.Kind == DownloadHealthStrikes.ClientScriptFailed);
        Assert.Equal("warning", record.Severity);
    }

    /// <summary>A script failure also reaches Activity, naming the script, so it is never a mystery what happened or why.</summary>
    [Fact]
    public async Task A_script_failure_is_written_to_activity_naming_the_script()
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(SabHistory("Failed", "Script was aborted, please check script_log for more information.", "CustomProcess.py"));

        var activity = await world.Jobs.ListActivityAsync(10, null, null, CancellationToken.None);
        var entry = Assert.Single(activity, item => item.Category == "download.client.script-failed");
        Assert.Contains("CustomProcess.py", entry.Message, StringComparison.Ordinal);
        Assert.Contains(Release, entry.Message, StringComparison.Ordinal);
    }

    /// <summary>A soft script warning is written to Activity too, never silently read as success.</summary>
    [Fact]
    public async Task A_script_warning_is_written_to_activity_naming_the_script()
    {
        using var world = await World.CreateAsync();

        await world.ShowHistoryAsync(SabHistory("Completed", "Script exit code 1, notify.sh reported a problem", "notify.sh"));

        var activity = await world.Jobs.ListActivityAsync(10, null, null, CancellationToken.None);
        var entry = Assert.Single(activity, item => item.Category == "download.client.script-warning");
        Assert.Contains("notify.sh", entry.Message, StringComparison.Ordinal);
    }

    private static DownloadClientHistoryItem SabHistory(string status, string failMessage, string scriptName)
    {
        var outcome = SabnzbdDownloadClient.NormalizeHistoryOutcome(status, failMessage);
        var errorMessage = outcome is DownloadClientHistoryOutcomes.ScriptFailed or DownloadClientHistoryOutcomes.ScriptWarning
            ? SabnzbdDownloadClient.ScriptReason(scriptName, isFailure: outcome == DownloadClientHistoryOutcomes.ScriptFailed)
            : null;
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
            SqliteBlockedReleaseRepository blocklist,
            SqliteDownloadHealthRepository health)
        {
            _storage = storage;
            Now = now;
            _service = service;
            _client = client;
            DispatchId = dispatchId;
            Jobs = jobs;
            Dispatches = dispatches;
            Blocklist = blocklist;
            Health = health;
        }

        public DateTimeOffset Now { get; }

        public string DispatchId { get; }

        public SqliteJobStore Jobs { get; }

        public SqliteDownloadDispatchesRepository Dispatches { get; }

        public SqliteBlockedReleaseRepository Blocklist { get; }

        public SqliteDownloadHealthRepository Health { get; }

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
            var health = new SqliteDownloadHealthRepository(storage.Factory, time);
            var service = new DownloadClientTelemetryService(
                settings, health, libraries, connections, null!, null!, time, null!,
                jobs, dispatches, jobs, new Deluno.Recovery.Policies.DownloadHealthEvaluator(), null!, Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance, blocklist);
            return new World(storage, now, service, client, dispatchId, jobs, dispatches, blocklist, health);
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
