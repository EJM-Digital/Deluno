using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Notifications;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Recovery.Services;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// #888: "give up after N days" is the one policy that needs a background
/// sweep rather than the ordinary retry-count table - these exercise that
/// sweep directly, separately from the retry-once/hold behaviour covered in
/// <see cref="ImportPipelineServiceTests"/>.
/// </summary>
public sealed class UnmatchedImportGiveUpServiceTests
{
    [Fact]
    public async Task RunAsync_does_nothing_when_the_policy_is_not_give_up_after_days()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var (platform, libraries, series, movies) = await SetUpAsync(storage, timeProvider);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var blockedReleases = new SqliteBlockedReleaseRepository(storage.Factory, timeProvider);

        var sourcePath = Path.Combine(storage.DataRoot, "downloads", "hold-default.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllTextAsync(sourcePath, "unmatched content");
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            library.Id, "tv", "series", "series-1", "Hold.Default.2026", "TestIndexer", "client-1", "TestClient", "sent",
            notesJson: null, cancellationToken: CancellationToken.None);
        await series.AddImportRecoveryCaseAsync(
            new CreateSeriesImportRecoveryCaseRequest(
                "Hold Default", ImportFailurePolicy.Unmatched, "Could not match.", "Import by hand.",
                $$"""{"DispatchId":"{{dispatchId}}","SourcePath":"{{sourcePath.Replace("\\", "\\\\")}}"}"""),
            CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromDays(30));
        var service = CreateGiveUpService(storage, timeProvider, platform, movies, series, dispatches, blockedReleases, new RecordingGateway());
        var handled = await service.RunAsync(CancellationToken.None);

        Assert.Equal(0, handled);
        Assert.True(File.Exists(sourcePath));
        Assert.Empty(await blockedReleases.ListKeysAsync(CancellationToken.None));
        Assert.NotEmpty((await series.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases);
    }

    [Fact]
    public async Task RunAsync_leaves_a_case_that_has_not_reached_the_configured_number_of_days()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var (platform, libraries, series, movies) = await SetUpAsync(storage, timeProvider, UnmatchedImportPolicies.GiveUpAfterDays, giveUpDays: 7);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var blockedReleases = new SqliteBlockedReleaseRepository(storage.Factory, timeProvider);

        var sourcePath = Path.Combine(storage.DataRoot, "downloads", "too-soon.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllTextAsync(sourcePath, "unmatched content");
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            library.Id, "tv", "series", "series-1", "Too.Soon.2026", "TestIndexer", "client-1", "TestClient", "sent",
            notesJson: null, cancellationToken: CancellationToken.None);
        await series.AddImportRecoveryCaseAsync(
            new CreateSeriesImportRecoveryCaseRequest(
                "Too Soon", ImportFailurePolicy.Unmatched, "Could not match.", "Import by hand.",
                $$"""{"DispatchId":"{{dispatchId}}","SourcePath":"{{sourcePath.Replace("\\", "\\\\")}}"}"""),
            CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromDays(2));
        var service = CreateGiveUpService(storage, timeProvider, platform, movies, series, dispatches, blockedReleases, new RecordingGateway());
        var handled = await service.RunAsync(CancellationToken.None);

        Assert.Equal(0, handled);
        Assert.True(File.Exists(sourcePath));
    }

    [Fact]
    public async Task RunAsync_gives_up_on_an_overdue_case_removing_deleting_and_blocklisting_it()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var (platform, libraries, series, movies) = await SetUpAsync(storage, timeProvider, UnmatchedImportPolicies.GiveUpAfterDays, giveUpDays: 3);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var blockedReleases = new SqliteBlockedReleaseRepository(storage.Factory, timeProvider);

        var sourcePath = Path.Combine(storage.DataRoot, "downloads", "overdue.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllTextAsync(sourcePath, "unmatched content");
        const string releaseName = "Overdue.Unmatched.2026";
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            library.Id, "tv", "series", "series-1", releaseName, "TestIndexer", "client-1", "TestClient", "sent",
            notesJson: null, cancellationToken: CancellationToken.None);
        // Set once the client shows the item, exactly as a real detection
        // poll would - needed for the "remove from client" step under test.
        await dispatches.RecordDetectionAsync(dispatchId, "torrent-hash-1", 1024, completed: true, CancellationToken.None);
        await series.AddImportRecoveryCaseAsync(
            new CreateSeriesImportRecoveryCaseRequest(
                "Overdue", ImportFailurePolicy.Unmatched, "Could not match.", "Import by hand.",
                $$"""{"DispatchId":"{{dispatchId}}","SourcePath":"{{sourcePath.Replace("\\", "\\\\")}}"}"""),
            CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromDays(4));
        var gateway = new RecordingGateway();
        var service = CreateGiveUpService(storage, timeProvider, platform, movies, series, dispatches, blockedReleases, gateway);
        var handled = await service.RunAsync(CancellationToken.None);

        Assert.Equal(1, handled);
        Assert.False(File.Exists(sourcePath), "the leftover unmatched file leaves the download folder once Deluno gives up on it");
        Assert.Contains(BlockedReleaseKeys.For(releaseName, "TestIndexer"), await blockedReleases.ListKeysAsync(CancellationToken.None));
        Assert.Equal("client-1", gateway.LastClientId);
        Assert.Empty((await series.GetImportRecoverySummaryAsync(CancellationToken.None)).RecentCases);
    }

    private static async Task InitializeAllAsync(TestStorage storage, TimeProvider timeProvider)
    {
        var migrator = new SqliteDatabaseMigrator(storage.Factory, timeProvider);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new MoviesSchemaInitializer(storage.Factory, migrator, NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new SeriesSchemaInitializer(storage.Factory, migrator, NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
    }

    /// <summary>
    /// #888: giving up on an unmatched import with a dispatch link queues a
    /// search for the exact title that wanted it, rather than leaving it to
    /// the library's own next missing/upgrade cycle.
    /// </summary>
    [Fact]
    public async Task RunAsync_queues_a_search_for_the_title_the_dispatch_names()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var (platform, libraries, series, movies) = await SetUpAsync(storage, timeProvider, UnmatchedImportPolicies.GiveUpAfterDays, giveUpDays: 3);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var blockedReleases = new SqliteBlockedReleaseRepository(storage.Factory, timeProvider);

        var sourcePath = Path.Combine(storage.DataRoot, "downloads", "search-again.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllTextAsync(sourcePath, "unmatched content");
        const string releaseName = "Search.Again.2026";
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            library.Id, "tv", "series", "series-search-again", releaseName, "TestIndexer", "client-1", "TestClient", "sent",
            notesJson: null, cancellationToken: CancellationToken.None);
        await dispatches.RecordDetectionAsync(dispatchId, "torrent-hash-2", 1024, completed: true, CancellationToken.None);
        await series.AddImportRecoveryCaseAsync(
            new CreateSeriesImportRecoveryCaseRequest(
                "Search Again", ImportFailurePolicy.Unmatched, "Could not match.", "Import by hand.",
                $$"""{"DispatchId":"{{dispatchId}}","SourcePath":"{{sourcePath.Replace("\\", "\\\\")}}"}"""),
            CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromDays(4));
        var service = CreateGiveUpService(
            storage, timeProvider, platform, movies, series, dispatches, blockedReleases, new RecordingGateway(),
            jobStore, libraries);
        var handled = await service.RunAsync(CancellationToken.None);

        Assert.Equal(1, handled);
        var jobs = await jobStore.ListAsync(200, CancellationToken.None);
        Assert.Contains(jobs, job =>
            job.JobType == LibrarySearchJobTypes.For("tv") &&
            job.PayloadJson != null &&
            job.PayloadJson.Contains("\"targetEntityId\":\"series-search-again\"", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// #966: filesystem.import.unmatched-given-up already landed in
    /// Activity but never reached the outbound path. Gated by its own
    /// preference, off by default (#936).
    /// </summary>
    [Fact]
    public async Task RunAsync_dispatches_unmatched_given_up_when_the_toggle_is_on()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var (platform, libraries, series, movies) = await SetUpAsync(storage, timeProvider, UnmatchedImportPolicies.GiveUpAfterDays, giveUpDays: 3);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var blockedReleases = new SqliteBlockedReleaseRepository(storage.Factory, timeProvider);

        var sourcePath = Path.Combine(storage.DataRoot, "downloads", "toggle-on.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllTextAsync(sourcePath, "unmatched content");
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            library.Id, "tv", "series", "series-toggle-on", "Toggle.On.2026", "TestIndexer", "client-1", "TestClient", "sent",
            notesJson: null, cancellationToken: CancellationToken.None);
        await series.AddImportRecoveryCaseAsync(
            new CreateSeriesImportRecoveryCaseRequest(
                "Toggle On", ImportFailurePolicy.Unmatched, "Could not match.", "Import by hand.",
                $$"""{"DispatchId":"{{dispatchId}}","SourcePath":"{{sourcePath.Replace("\\", "\\\\")}}"}"""),
            CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromDays(4));
        var outbound = new RecordingOutboundNotificationService();
        var preferences = new SqliteNotificationService(storage.Factory, timeProvider);
        var current = await preferences.GetPreferencesAsync(CancellationToken.None);
        current.UnmatchedImportGivenUpEnabled = true;
        await preferences.UpdatePreferencesAsync(current, CancellationToken.None);
        var service = CreateGiveUpService(
            storage, timeProvider, platform, movies, series, dispatches, blockedReleases, new RecordingGateway(),
            outboundNotificationService: outbound, notificationPreferences: preferences);

        var handled = await service.RunAsync(CancellationToken.None);

        Assert.Equal(1, handled);
        Assert.Contains("filesystem.import.unmatched-given-up", outbound.DispatchedCategories);
    }

    [Fact]
    public async Task RunAsync_stays_silent_when_the_toggle_is_off()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T08:00:00Z"));
        await InitializeAllAsync(storage, timeProvider);
        var (platform, libraries, series, movies) = await SetUpAsync(storage, timeProvider, UnmatchedImportPolicies.GiveUpAfterDays, giveUpDays: 3);
        var library = Assert.Single(await libraries.ListLibrariesAsync(CancellationToken.None));
        var jobStore = new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository());
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, timeProvider);
        var blockedReleases = new SqliteBlockedReleaseRepository(storage.Factory, timeProvider);

        var sourcePath = Path.Combine(storage.DataRoot, "downloads", "toggle-off.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        await File.WriteAllTextAsync(sourcePath, "unmatched content");
        var dispatchId = await jobStore.RecordDownloadDispatchAsync(
            library.Id, "tv", "series", "series-toggle-off", "Toggle.Off.2026", "TestIndexer", "client-1", "TestClient", "sent",
            notesJson: null, cancellationToken: CancellationToken.None);
        await series.AddImportRecoveryCaseAsync(
            new CreateSeriesImportRecoveryCaseRequest(
                "Toggle Off", ImportFailurePolicy.Unmatched, "Could not match.", "Import by hand.",
                $$"""{"DispatchId":"{{dispatchId}}","SourcePath":"{{sourcePath.Replace("\\", "\\\\")}}"}"""),
            CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromDays(4));
        var outbound = new RecordingOutboundNotificationService();
        var preferences = new SqliteNotificationService(storage.Factory, timeProvider);
        var service = CreateGiveUpService(
            storage, timeProvider, platform, movies, series, dispatches, blockedReleases, new RecordingGateway(),
            outboundNotificationService: outbound, notificationPreferences: preferences);

        var handled = await service.RunAsync(CancellationToken.None);

        Assert.Equal(1, handled);
        Assert.DoesNotContain("filesystem.import.unmatched-given-up", outbound.DispatchedCategories);
    }

    private sealed class RecordingOutboundNotificationService : IOutboundNotificationService
    {
        public List<string> DispatchedCategories { get; } = [];

        public Task DispatchAsync(string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken, IReadOnlyList<string>? titleTags = null)
        {
            DispatchedCategories.Add(eventCategory);
            return Task.CompletedTask;
        }

        public Task<Notifications.Contracts.NotificationWebhookDeliveryResult?> DispatchToWebhookAsync(string webhookId, string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Notifications.Contracts.NotificationWebhookDeliveryResult?> ReplayAsync(string deliveryId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private static async Task<(SqlitePlatformSettingsRepository Platform, SqliteLibrariesRepository Libraries, SqliteSeriesCatalogRepository Series, SqliteMovieCatalogRepository Movies)> SetUpAsync(
        TestStorage storage,
        TimeProvider timeProvider,
        string policy = "hold",
        int giveUpDays = 7)
    {
        var platform = new SqlitePlatformSettingsRepository(storage.Factory, timeProvider, TestSecretProtection.Create(storage));
        var libraries = new SqliteLibrariesRepository(storage.Factory, timeProvider);
        var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
        var seriesRootPath = Path.Combine(storage.DataRoot, "series");
        Directory.CreateDirectory(downloadsPath);
        Directory.CreateDirectory(seriesRootPath);

        await platform.SaveAsync(
            PlatformSettingsPatchMerger.Apply(
                await platform.GetAsync(CancellationToken.None),
                new PatchPlatformSettingsRequest(
                    SeriesRootPath: seriesRootPath,
                    DownloadsPath: downloadsPath,
                    UnmatchedImportPolicy: policy,
                    UnmatchedImportGiveUpDays: giveUpDays)),
            CancellationToken.None);

        await libraries.CreateLibraryAsync(
            new CreateLibraryRequest(
                Name: "TV", MediaType: "tv", Purpose: "Main", RootPath: seriesRootPath, DownloadsPath: downloadsPath,
                QualityProfileId: null, ImportWorkflow: "standard", ProcessorName: null, ProcessorOutputPath: null,
                ProcessorTimeoutMinutes: null, ProcessorFailureMode: null, AutoSearchEnabled: true,
                MissingSearchEnabled: true, UpgradeSearchEnabled: true, SearchIntervalMinutes: 6, RetryDelayHours: 24,
                MaxItemsPerRun: 25),
            CancellationToken.None);

        var series = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
        var movies = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        return (platform, libraries, series, movies);
    }

    private static UnmatchedImportGiveUpService CreateGiveUpService(
        TestStorage storage,
        TimeProvider timeProvider,
        SqlitePlatformSettingsRepository platform,
        SqliteMovieCatalogRepository movies,
        SqliteSeriesCatalogRepository series,
        SqliteDownloadDispatchesRepository dispatches,
        SqliteBlockedReleaseRepository blockedReleases,
        IDownloadClientActionGateway gateway,
        SqliteJobStore? jobStore = null,
        SqliteLibrariesRepository? libraries = null,
        IOutboundNotificationService? outboundNotificationService = null,
        INotificationService? notificationPreferences = null)
        => new(
            platform,
            movies,
            series,
            dispatches,
            blockedReleases,
            gateway,
            jobStore ?? new SqliteJobStore(storage.Factory, timeProvider, new NullRealtimeEventPublisher(), new NullDownloadDispatchesRepository()),
            timeProvider,
            NullLogger<UnmatchedImportGiveUpService>.Instance,
            jobScheduler: jobStore,
            // #1318/#1319: a file only ever leaves through the bin, and only when Deluno can tell where it is.
            librariesRepository: libraries ?? new SqliteLibrariesRepository(storage.Factory, timeProvider),
            outboundNotificationService: outboundNotificationService,
            notificationPreferences: notificationPreferences,
            recycleBinService: new Deluno.Platform.RecycleBinService(
                Microsoft.Extensions.Options.Options.Create(new Deluno.Infrastructure.Storage.StoragePathOptions { DataRoot = storage.DataRoot }),
                timeProvider,
                NullLogger<Deluno.Platform.RecycleBinService>.Instance));

    private sealed class RecordingGateway : IDownloadClientActionGateway
    {
        public string? LastClientId { get; private set; }
        public string? LastQueueItemId { get; private set; }

        public Task<DownloadClientRemovalResult> RemoveWithDataAsync(string clientId, string queueItemId, CancellationToken cancellationToken)
        {
            LastClientId = clientId;
            LastQueueItemId = queueItemId;
            return Task.FromResult(new DownloadClientRemovalResult(true, "Removed."));
        }

        public Task<DownloadClientRemovalResult> StopSeedingAsync(string clientId, string queueItemId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
