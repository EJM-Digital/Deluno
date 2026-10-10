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
using Deluno.Recovery.Policies;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #560: "Block this release: Do not select the same failed release again".
///
/// <para>The strike policy used to keep its own list, checked only when a grab
/// was sent. The search did not know about it, so it kept choosing the blocked
/// release, and no screen showed the block or could lift it. Now a download
/// that stopped long enough is refused on the one blocklist: searches skip it,
/// the title page counts it, and the blocklist screen lifts it.</para>
/// </summary>
public sealed class AStoppedDownloadIsRefusedOnTheBlocklistTests
{
    private const string Release = "Arrival.2016.1080p.WEB";

    [Fact]
    public async Task A_download_stopped_past_the_threshold_is_refused_where_searches_and_people_see_it()
    {
        using var world = await World.CreateAsync();

        var report = await world.RemediateAsync(DownloadHealthStrikes.ClientStalled, strikes: 3);

        Assert.Equal(1, report.ReleasesRefused);
        var refused = Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Equal(ImportFailurePolicy.DownloadStalled, refused.ReasonCode);
        Assert.Equal(BlockedReleaseStates.Refused, refused.State);
        Assert.Equal("movie-arrival", refused.EntityId);
        // #800: Deluno decided this on its own, from a health strike, not a
        // person clicking Refuse.
        Assert.Equal(BlockedReleaseOrigins.Automatic, refused.Origin);
        Assert.Equal($"qBittorrent on {Deluno.Contracts.DelunoHostName.Current} showed it stalled or in error for an hour, so Deluno stopped waiting for it and will not pick it again.", refused.Reason);
        var keys = await world.Blocklist.ListKeysAsync(CancellationToken.None);
        Assert.Contains(BlockedReleaseKeys.For(Release, "Fixture"), keys);
        // #874: a stall holds the release from every indexer too, not only
        // the one it stalled on — the same release offered by a different
        // indexer, under the same name, is refused just the same.
        Assert.Contains(BlockedReleaseKeys.ForEveryIndexer(Release), keys);
        // Nothing was imported, so nothing says the copy is junk: it stays in the client.
        Assert.Empty(await world.Blocklist.ListAwaitingCleanupAsync(CancellationToken.None));
        Assert.Equal(1, report.ReplacementSearchesQueued);
    }

    [Fact]
    public async Task A_release_is_refused_once_however_often_the_pass_runs()
    {
        using var world = await World.CreateAsync();

        await world.RemediateAsync(DownloadHealthStrikes.ClientStalled, strikes: 3);
        var again = await world.RemediateAsync(DownloadHealthStrikes.ClientStalled, strikes: 4);

        Assert.Equal(0, again.ReleasesRefused);
        Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Single(await world.Jobs.ListAsync(10, CancellationToken.None));
    }

    [Theory]
    [InlineData("import-failed")]
    [InlineData("post-processing-failed")]
    [InlineData("excessive-eta")]
    public async Task A_finding_that_is_not_a_stopped_download_refuses_nothing(string kind)
    {
        using var world = await World.CreateAsync();

        var report = await world.RemediateAsync(kind, strikes: 5);

        Assert.Equal(0, report.Evaluated);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Empty(await world.Jobs.ListAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task Below_the_threshold_it_is_still_waited_for()
    {
        using var world = await World.CreateAsync();

        var report = await world.RemediateAsync(DownloadHealthStrikes.NoThroughput, strikes: 2);

        Assert.Equal(0, report.Evaluated);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(DownloadHealthStrikes.ClientStalled, 1, "qBittorrent showed it stalled or in error, so Deluno")]
    [InlineData(DownloadHealthStrikes.ClientStalled, 2, "qBittorrent showed it stalled or in error for 30 minutes, so Deluno")]
    [InlineData(DownloadHealthStrikes.ClientStalled, 3, "qBittorrent showed it stalled or in error for an hour, so Deluno")]
    [InlineData(DownloadHealthStrikes.ClientStalled, 5, "qBittorrent showed it stalled or in error for 2 hours, so Deluno")]
    // A rule's removal speaks for that rule, in checks (#1206), never the client's stall sentence.
    [InlineData(DownloadHealthStrikes.NoThroughput, 3, "It made no progress for 3 checks in a row, so Deluno")]
    public void The_reason_says_how_long_it_went_on(string kind, int strikes, string says)
        => Assert.StartsWith(says, DownloadClientTelemetryService.StalledReason(Finding(kind, strikes), "qBittorrent"), StringComparison.Ordinal);

    /// <summary>
    /// Golden path rc.30.8: a rule set to strike every minute had gone on for fifteen minutes and Activity said
    /// "4 hours". The message now says how long the spell really lasted.
    /// </summary>
    [Theory]
    [InlineData(0, "qBittorrent showed it stalled or in error, so Deluno")]
    [InlineData(1, "qBittorrent showed it stalled or in error for a minute, so Deluno")]
    [InlineData(15, "qBittorrent showed it stalled or in error for 15 minutes, so Deluno")]
    [InlineData(60, "qBittorrent showed it stalled or in error for an hour, so Deluno")]
    [InlineData(150, "qBittorrent showed it stalled or in error for 2.5 hours, so Deluno")]
    public void The_reason_says_how_long_it_has_really_been_stalled(int minutes, string says)
    {
        var now = DateTimeOffset.Parse("2026-10-08T12:00:00Z");
        var finding = Finding(DownloadHealthStrikes.ClientStalled, 9) with { SpellStartedUtc = now.AddMinutes(-minutes) };

        var reason = DownloadClientTelemetryService.StalledReason(finding, "qBittorrent", now);

        Assert.StartsWith(says, reason, StringComparison.Ordinal);
        Assert.DoesNotContain("4 hours", reason);
    }

    /// <summary>#1167: Activity says what Deluno did in plain words, not the internal list of steps.</summary>
    [Fact]
    public void Activity_says_what_Deluno_did_about_a_stalled_download_in_plain_words()
        => Assert.Equal(
            "Deluno blocked this release, is looking for another copy and removed it from qBittorrent.",
            DownloadClientTelemetryService.PlainRemediation(["release refused", "replacement search queued", "client entry removed (payload retained by client)"], "qBittorrent"));

    [Theory]
    [InlineData(8_640_000L, 0)]
    [InlineData(9_000_000L, 0)]
    [InlineData(null, 0)]
    [InlineData(-1L, 0)]
    [InlineData(3_600L, 3_600)]
    public void qBittorrent_saying_it_has_no_estimate_is_an_unknown_estimate(long? eta, int seconds)
        => Assert.Equal(seconds, QbittorrentDownloadClient.EtaSeconds(eta));

    private static DownloadHealthFinding Finding(string kind, int strikes)
        => new("critical", kind, "Stopped", "Queue status: stalled.", "Review", false, false, StrikeCount: strikes);

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;
        private readonly FixedTimeProvider _time;
        private readonly DownloadClientTelemetryService _service;
        private readonly DownloadClientItem _client;

        private World(TestStorage storage, FixedTimeProvider time, DownloadClientTelemetryService service, DownloadClientItem client, SqliteJobStore jobs, SqliteBlockedReleaseRepository blocklist)
        {
            _storage = storage;
            _time = time;
            _service = service;
            _client = client;
            Jobs = jobs;
            Blocklist = blocklist;
        }

        public SqliteJobStore Jobs { get; }

        public SqliteBlockedReleaseRepository Blocklist { get; }

        public static async Task<World> CreateAsync()
        {
            var storage = TestStorage.Create();
            var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-22T10:00:00Z"));
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            var settings = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            await AnInstallThatHasTheStrikeActionsOn.ApplyAsync(settings);
            var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var libraries = new SqliteLibrariesRepository(storage.Factory, time);
            var library = await libraries.CreateLibraryAsync(new CreateLibraryRequest(
                "Movies", "movies", "library", Path.Combine(storage.DataRoot, "movies"), Path.Combine(storage.DataRoot, "downloads"), null,
                "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
            var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
                "External qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "deluno-movies", "deluno-tv", null, 1, true), CancellationToken.None);
            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-arrival", Release, "Fixture", client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(dispatchId, "queue-arrival", 1024, completed: false, CancellationToken.None);
            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            // #904: CleanupRemoveClientEntryAfterThreshold now defaults to on, so
            // remediation reaches the download-client registry even in these
            // blocklist-focused tests. An empty registry behaves the way a real
            // one does for a protocol it has no adapter for: TryGet fails and the
            // removal is recorded as not done, rather than throwing.
            var service = new DownloadClientTelemetryService(
                settings, new SqliteDownloadHealthRepository(storage.Factory, time), libraries, connections, null!, new DownloadClientRegistry([]), time, null!,
                jobs, dispatches, jobs, new DownloadHealthEvaluator(), null!, Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance, blocklist);
            return new World(storage, time, service, client, jobs, blocklist);
        }

        public Task<DownloadHealthRemediationReport> RemediateAsync(string kind, int strikes)
        {
            var item = new DownloadQueueItem("queue-arrival", _client.Id, _client.Name, _client.Protocol, "movies", "Arrival", Release,
                "deluno-movies", DownloadQueueStatuses.Stalled, 15, 0, 0, 1024, 128, 0, "Fixture", null, _time.GetUtcNow(), HealthFindings: [Finding(kind, strikes)]);
            var snapshot = new DownloadClientTelemetrySnapshot(_client.Id, _client.Name, _client.Protocol, null, "healthy", null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "none"),
                new DownloadTelemetrySummary(0, 0, 0, 1, 0, 0, 0), [item], [], _time.GetUtcNow());
            return _service.RunConfiguredHealthRemediationAsync(
                new DownloadTelemetryOverview(snapshot.Summary, [snapshot], _time.GetUtcNow()), CancellationToken.None);
        }

        public void Dispose() => _storage.Dispose();
    }
}
