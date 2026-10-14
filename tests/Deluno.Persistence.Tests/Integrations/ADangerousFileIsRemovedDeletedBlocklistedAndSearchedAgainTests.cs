using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
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
/// #879 (cleanup sheet C6): end-to-end proof that a download flagged by the
/// content blocklist (#816) is removed from the client, its files are
/// deleted, the release is blocklisted, and a replacement search is queued -
/// not merely shown as a warning, which is all Deluno did before this issue.
///
/// <para>Runs the real <see cref="DownloadHealthEvaluator"/> against a queue
/// item whose file listing carries a blocked file name, so the finding this
/// test acts on is the one #816's own file-listing feeds it, not a
/// hand-built stand-in for it. That finding is then fed to
/// <see cref="DownloadClientTelemetryService.RunConfiguredHealthRemediationAsync"/>,
/// the same removal gate every other health rule goes through, against real
/// SQLite repositories and a fake download client that records what it was
/// asked to do.</para>
/// </summary>
public sealed class ADangerousFileIsRemovedDeletedBlocklistedAndSearchedAgainTests
{
    private const string Release = "Example.Movie.2026.1080p.WEB-DL-GROUP";

    /// <summary>A real data folder with the real databases in it: every test here runs on one, and its download folder holds a real file.</summary>
    private static TestStorage NewStorage() => TestStorage.Create();

    [Fact]
    public async Task A_dangerous_file_inside_an_otherwise_clean_release_is_removed_deleted_blocklisted_and_searched_again()
    {
        using var world = await World.CreateAsync();

        var report = await world.RemediateAsync(files: ["Example.Movie.2026.mkv", "Setup.exe"]);

        // Removed from the client together with its data (the client deletes its own copy; Deluno then sweeps
        // whatever is left below).
        Assert.Equal(1, report.ClientEntriesRemoved);
        Assert.Equal(DownloadClientActions.DeleteWithData, world.FakeDownloadClient.LastAction);
        Assert.Equal("queue-movie", world.FakeDownloadClient.LastQueueItemId);

        // Its files deleted.
        Assert.Equal(1, report.PayloadsPurged);
        Assert.False(File.Exists(world.PayloadPath));

        // The release blocklisted, so search never offers it again.
        var refused = Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Equal(BlockedReleaseStates.Refused, refused.State);
        Assert.Equal("movie-example", refused.EntityId);

        // A replacement search queued, without anyone clicking.
        Assert.Equal(1, report.ReplacementSearchesQueued);
        Assert.Single(await world.Jobs.ListAsync(10, CancellationToken.None));

        // James's note (#879): fully transparent in Activity, not a silent action.
        var activity = await world.Jobs.ListActivityAsync(10, null, null, CancellationToken.None);
        var entry = Assert.Single(activity, item => item.Category == "download.health.remediated");
        Assert.Contains(Release, entry.Message);
        Assert.Contains("Dangerous or junk file", entry.Message);
    }

    [Fact]
    public async Task A_clean_release_with_no_blocked_file_is_left_alone()
    {
        using var world = await World.CreateAsync();

        var report = await world.RemediateAsync(files: ["Example.Movie.2026.mkv", "readme.txt"]);

        Assert.Equal(0, report.Evaluated);
        Assert.Equal(0, report.ClientEntriesRemoved);
        Assert.Null(world.FakeDownloadClient.LastAction);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.True(File.Exists(world.PayloadPath));
    }

    [Fact]
    public async Task An_outage_leaves_a_dangerous_file_finding_alone_until_it_ends()
    {
        // #901: the same outage guard every other health rule goes through -
        // nothing here can tell a bad release apart from every release
        // simply being unable to reach anything while the internet is down,
        // so the whole pass is skipped, dangerous-file findings included.
        using var world = await World.CreateAsync(isOutage: true);

        var report = await world.RemediateAsync(files: ["Example.Movie.2026.mkv", "Setup.exe"]);

        Assert.Equal(0, report.Evaluated);
        Assert.Equal(0, report.ClientEntriesRemoved);
        Assert.Null(world.FakeDownloadClient.LastAction);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.True(File.Exists(world.PayloadPath));

        world.Connectivity.IsOutage = false;
        var afterOutage = await world.RemediateAsync(files: ["Example.Movie.2026.mkv", "Setup.exe"]);

        Assert.Equal(1, afterOutage.ClientEntriesRemoved);
        Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
    }
    /// <summary>
    /// A release carrying an executable is never left on disk (#1167): the
    /// rule that acts on it removes the
    /// client entry together with its data, deletes the leftovers and
    /// blocklists the release.
    /// </summary>
    [Theory]
    [InlineData("Setup.exe")]
    [InlineData("Setup.scr")]
    [InlineData("install.bat")]
    [InlineData("Free Movie.lnk")]
    public async Task A_dangerous_payload_is_removed_with_its_files_and_blocklisted(string dangerous)
    {
        using var world = await World.CreateAsync();

        var report = await world.RemediateAsync(files: ["Example.Movie.2026.mkv", dangerous], blocked: ["*.exe", "*.scr", "*.bat", "*.lnk"]);

        Assert.Equal(1, report.ClientEntriesRemoved);
        Assert.Equal(DownloadClientActions.DeleteWithData, world.FakeDownloadClient.LastAction);
        Assert.Equal(1, report.PayloadsPurged);
        Assert.False(File.Exists(world.PayloadPath));
        Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
    }

    /// <summary>The rig's 908 MB fake release: removing a refused download takes its files with it.</summary>
    [Fact]
    public async Task Removing_a_refused_download_takes_its_files_with_the_client_entry()
    {
        using var world = await World.CreateAsync();

        var report = await world.RemediateAsync(files: ["Example.Movie.2026.mkv", "Setup.exe"]);

        Assert.Equal(DownloadClientActions.DeleteWithData, world.FakeDownloadClient.LastAction);
        Assert.Equal(1, report.ClientEntriesRemoved);
        Assert.False(File.Exists(world.PayloadPath));
    }


    /// <summary>
    /// "Just tell me" is the rule action that only says so: a download struck out under it is not removed, its files are not
    /// deleted, nothing is blocklisted and no search is queued, even with every cleanup switch on.
    /// </summary>
    [Fact]
    public async Task A_slow_download_struck_out_under_a_just_tell_me_rule_is_only_reported_and_nothing_is_touched()
    {
        using var world = await World.CreateAsync();

        var report = await world.RemediateAsync(
            files: ["Example.Movie.2026.mkv"],
            kind: DownloadHealthRuleKinds.TooSlow,
            actionPreset: DownloadHealthActionPresets.JustTellMe,
            speedMbps: 0.001);

        Assert.Equal(0, report.ClientEntriesRemoved);
        Assert.Equal(0, report.PayloadsPurged);
        Assert.Equal(0, report.ReplacementSearchesQueued);
        Assert.Null(world.FakeDownloadClient.LastAction);
        Assert.True(File.Exists(world.PayloadPath), "just tell me never deletes a file");
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Empty(await world.Jobs.ListAsync(10, CancellationToken.None));
    }

    /// <summary>
    /// "Retry elsewhere, keep files": the release is refused and another is searched for, but the struck download stays in the
    /// client with its files, so a person (or the client) can still use what was fetched.
    /// </summary>
    [Fact]
    public async Task A_slow_download_struck_out_under_a_retry_elsewhere_rule_is_blocklisted_and_searched_again_but_left_in_the_client_with_its_files()
    {
        using var world = await World.CreateAsync();

        var report = await world.RemediateAsync(
            files: ["Example.Movie.2026.mkv"],
            kind: DownloadHealthRuleKinds.TooSlow,
            actionPreset: DownloadHealthActionPresets.RetryElsewhere,
            speedMbps: 0.001);

        var refused = Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Equal(BlockedReleaseStates.Refused, refused.State);
        Assert.Equal(1, report.ReplacementSearchesQueued);
        Assert.Single(await world.Jobs.ListAsync(10, CancellationToken.None));
        Assert.Equal(0, report.ClientEntriesRemoved);
        Assert.Equal(0, report.PayloadsPurged);
        Assert.Null(world.FakeDownloadClient.LastAction);
        Assert.True(File.Exists(world.PayloadPath), "keep files means the download is left exactly where it is");
    }

    /// <summary>Records the last action asked of it, without touching any real client.</summary>
    private sealed class FakeClient(string protocol) : IDownloadClient
    {
        public string Protocol => protocol;

        public string? LastAction { get; private set; }

        public string? LastQueueItemId { get; private set; }

        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, true, true, protocol);

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
        {
            LastAction = action;
            LastQueueItemId = queueItemId;
            return Task.FromResult(new DownloadClientActionResult(client.Id, queueItemId, action, true, $"{action} sent to {client.Name}"));
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

    /// <summary>The same fake used by <see cref="AnOutageStopsCleanupAndSearchTests"/>, toggled per test.</summary>
    private sealed class FakeConnectivityMonitor : Deluno.Contracts.Connectivity.IConnectivityMonitor
    {
        public bool IsOutage { get; set; }

        public Task<Deluno.Contracts.Connectivity.ConnectivityStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Current);

        public Deluno.Contracts.Connectivity.ConnectivityStatus Current => new(
            IsOutage,
            IsOutage ? DateTimeOffset.Parse("2026-09-28T09:00:00Z") : null,
            DateTimeOffset.Parse("2026-09-28T10:00:00Z"),
            ["1.1.1.1:443"],
            IsOutage ? "Internet down since 09:00 — cleanup and searching paused." : "Connected.");

        public Task<TimeSpan> OutageOverlapAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
            => Task.FromResult(TimeSpan.Zero);
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;
        private readonly FixedTimeProvider _time;
        private readonly DownloadClientTelemetryService _service;
        private readonly DownloadClientItem _client;
        private readonly LibraryItem _library;

        private World(
            TestStorage storage, FixedTimeProvider time, DownloadClientTelemetryService service,
            DownloadClientItem client, LibraryItem library, SqliteJobStore jobs, SqliteBlockedReleaseRepository blocklist,
            FakeClient fakeClient, string payloadPath, FakeConnectivityMonitor connectivity)
        {
            _storage = storage;
            _time = time;
            _service = service;
            _client = client;
            _library = library;
            Jobs = jobs;
            Blocklist = blocklist;
            FakeDownloadClient = fakeClient;
            PayloadPath = payloadPath;
            Connectivity = connectivity;
        }

        public FakeClient FakeDownloadClient { get; }

        public SqliteJobStore Jobs { get; }

        public SqliteBlockedReleaseRepository Blocklist { get; }

        public string PayloadPath { get; }

        public FakeConnectivityMonitor Connectivity { get; }

        public static Task<World> CreateAsync() => CreateAsync(isOutage: false);

        public static async Task<World> CreateAsync(bool isOutage, bool cleanupSwitchesOn = true)
        {
            var storage = NewStorage();
            var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-28T10:00:00Z"));
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            var settings = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var libraries = new SqliteLibrariesRepository(storage.Factory, time);
            var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
            var library = await libraries.CreateLibraryAsync(new CreateLibraryRequest(
                "Movies", "movies", "library", Path.Combine(storage.DataRoot, "movies"), downloadsPath, null,
                "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
            Directory.CreateDirectory(downloadsPath);
            var payloadPath = Path.Combine(downloadsPath, "Example.Movie.2026.mkv");
            await File.WriteAllTextAsync(payloadPath, "fixture payload");

            var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
                "External qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "deluno-movies", "deluno-tv", null, 1, true), CancellationToken.None);
            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-example", Release, "Fixture", client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(dispatchId, "queue-movie", 1024, completed: false, CancellationToken.None);
            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var fakeClient = new FakeClient("qbittorrent");

            // Cleanup-sheet C6 (#879): remove with data, blocklist and search
            // again, all on by default for this scenario's own test.
            var currentSettings = await settings.GetAsync(CancellationToken.None);
            await settings.SaveAsync(
                PlatformSettingsPatchMerger.Apply(
                    currentSettings,
                    new PatchPlatformSettingsRequest(
                        CleanupBlockReleaseAfterThreshold: cleanupSwitchesOn,
                        CleanupQueueReplacementAfterThreshold: cleanupSwitchesOn,
                        CleanupRemoveClientEntryAfterThreshold: cleanupSwitchesOn,
                        CleanupPurgePayloadAfterThreshold: cleanupSwitchesOn,
                        DownloadHealthDangerousFileEnabled: true)),
                CancellationToken.None);

            var connectivity = new FakeConnectivityMonitor { IsOutage = isOutage };
            var service = new DownloadClientTelemetryService(
                settings, new SqliteDownloadHealthRepository(storage.Factory, time), libraries, connections, null!,
                new DownloadClientRegistry([fakeClient]), time, null!,
                jobs, dispatches, jobs, new DownloadHealthEvaluator(), null!, connectivity, blocklist);
            return new World(storage, time, service, client, library, jobs, blocklist, fakeClient, payloadPath, connectivity);
        }

        public async Task<DownloadHealthRemediationReport> RemediateAsync(
            IReadOnlyList<string> files,
            bool isSetupProblem = false,
            IReadOnlyList<string>? blocked = null,
            string kind = DownloadHealthRuleKinds.DangerousFile,
            string actionPreset = DownloadHealthActionPresets.Replace,
            double speedMbps = 5)
        {
            var rules = DownloadHealthRules.Defaults.Append(new DownloadHealthRule(
                Id: "test-rule",
                Kind: kind,
                Enabled: true,
                StrikeLimit: 1,
                TimerMinutes: 1,
                AppliesTo: DownloadHealthAppliesTo.Both,
                MinCompletionPercent: 0,
                MaxCompletionPercent: 100,
                ResetStrikesOnProgress: true,
                // Matches the real "legacy-dangerous-file" migrated rule's own
                // preset (SqliteDownloadHealthRepository.LegacyActionPreset):
                // remove, delete, blocklist and search again - still gated by
                // the #879 override below, which requires the platform's own
                // cleanup switches independently of this preset.
                ActionPreset: actionPreset,
                CreatedUtc: _time.GetUtcNow(),
                UpdatedUtc: _time.GetUtcNow(),
                // The speed floor a too-slow rule reads, in KB/s; unused by the other kinds.
                Threshold: 500)).ToArray();

            var contentBlocklist = new ContentBlocklistSettings(
                Enabled: true,
                Mode: ContentBlocklistModes.Blacklist,
                UseBuiltInPatterns: false,
                CustomPatterns: blocked ?? ["*.exe"],
                RemoteUrl: null,
                RemotePatterns: [],
                LastRefreshedUtc: null,
                UpdatedUtc: _time.GetUtcNow());

            // The real #816 evaluator, fed the file listing a client's own
            // GetFilesAsync would have produced, so the finding driving this
            // gate is the genuine content-blocklist finding, not a stand-in.
            var recoverySnapshot = new RecoveryQueueSnapshot(
                Status: "downloading",
                ErrorMessage: null,
                SourcePath: PayloadPath,
                SpeedMbps: speedMbps,
                AddedUtc: _time.GetUtcNow().AddMinutes(-10),
                EtaSeconds: 60,
                ReleaseName: Release,
                IsSetupProblem: isSetupProblem)
            {
                Files = files
            };
            var recoveryFindings = new DownloadHealthEvaluator().Evaluate(recoverySnapshot, _time.GetUtcNow(), rules, contentBlocklist);
            // The evaluator answers in its own RecoveryHealthFinding shape;
            // the queue item stores DownloadHealthFinding, with its own
            // StrikeCount already at the matched rule's strike limit, the
            // same way a real pass's strike bookkeeping would leave it once
            // this dangerous-file rule's instant (1-strike) limit is hit on
            // its first sighting.
            var findings = recoveryFindings.Select(finding => new DownloadHealthFinding(
                finding.Severity, finding.Kind, finding.Summary, finding.Evidence, finding.RecommendedAction,
                finding.CanSafelyRetry, finding.CanSafelyRemove,
                StrikeCount: finding.RuleStrikeLimit ?? 1,
                RuleId: finding.RuleId,
                RuleActionPreset: finding.RuleActionPreset,
                RuleStrikeLimit: finding.RuleStrikeLimit)).ToArray();

            var item = new DownloadQueueItem(
                "queue-movie", _client.Id, _client.Name, _client.Protocol, "movies", "Example Movie", Release,
                "deluno-movies", DownloadQueueStatuses.Downloading, 40, 5, 60, 4_000_000_000, 1_600_000_000, 0, "Fixture", null,
                _time.GetUtcNow(), PayloadPath, HealthFindings: findings);
            var snapshot = new DownloadClientTelemetrySnapshot(_client.Id, _client.Name, _client.Protocol, null, "healthy", null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "none"),
                new DownloadTelemetrySummary(0, 0, 0, 1, 0, 0, 0), [item], [], _time.GetUtcNow());
            return await _service.RunConfiguredHealthRemediationAsync(
                new DownloadTelemetryOverview(snapshot.Summary, [snapshot], _time.GetUtcNow()), CancellationToken.None);
        }

        public void Dispose() => _storage.Dispose();
    }
}
