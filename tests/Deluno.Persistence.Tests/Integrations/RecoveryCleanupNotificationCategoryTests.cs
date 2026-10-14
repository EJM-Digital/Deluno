using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Resilience;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Notifications;
using Deluno.Notifications.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #858: Deluno's own recovery/cleanup actions (queue removal, orphaned
/// download removal) already land in Activity; this covers the same
/// actions also reaching the outbound path, gated by their own preference
/// and off by default so nothing new fires unannounced.
/// </summary>
public sealed class RecoveryCleanupNotificationCategoryTests
{
    private const string Release = "Some.Movie.2024.1080p.WEB-DL-GROUP";
    private const string Indexer = "TestIndexer";
    private const string QueueItemId = "queue-item-1";

    [Fact]
    public async Task Removing_a_queue_item_dispatches_download_queue_removed_when_the_toggle_is_on()
    {
        using var storage = TestStorage.Create();
        var world = await World.CreateAsync(storage, queueItemRemovedEnabled: true);

        var result = await world.Service.RemoveWithReasonAsync(world.ClientId, QueueItemId, QueueRemovalReasons.Unwanted, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("download.queue.removed", world.Outbound.DispatchedCategories);
    }

    [Fact]
    public async Task Removing_a_queue_item_stays_silent_when_the_toggle_is_off()
    {
        using var storage = TestStorage.Create();
        var world = await World.CreateAsync(storage, queueItemRemovedEnabled: false);

        var result = await world.Service.RemoveWithReasonAsync(world.ClientId, QueueItemId, QueueRemovalReasons.Unwanted, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain("download.queue.removed", world.Outbound.DispatchedCategories);
    }

    [Fact]
    public async Task Removing_an_orphaned_download_dispatches_download_orphan_removed_when_the_toggle_is_on()
    {
        using var storage = TestStorage.Create();
        var world = await World.CreateAsync(storage, queueItemRemovedEnabled: false, orphanedDownloadRemovedEnabled: true);
        var path = Path.Combine(storage.DataRoot, "downloads", Release);
        Directory.CreateDirectory(path);
        Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromHours(3));
        var orphanedService = new OrphanedDownloadService(
            world.Libraries,
            world.Settings,
            world.Service,
            world.Jobs,
            world.Processors,
            world.Kept,
            world.Activity,
            TimeProvider.System,
            NullLogger<OrphanedDownloadService>.Instance,
            world.Outbound,
            world.Preferences,
            recycleBinService: new Deluno.Platform.RecycleBinService(Microsoft.Extensions.Options.Options.Create(new Deluno.Infrastructure.Storage.StoragePathOptions { DataRoot = storage.DataRoot }), TimeProvider.System, NullLogger<Deluno.Platform.RecycleBinService>.Instance));

        var action = await orphanedService.RemoveAsync(path, CancellationToken.None);

        Assert.True(action.Done, action.Message);
        Assert.False(Directory.Exists(path), "the folder left the download folder");
        Assert.Contains("download.orphan.removed", world.Outbound.DispatchedCategories);
    }

    [Fact]
    public async Task Removing_an_orphaned_download_stays_silent_when_the_toggle_is_off()
    {
        using var storage = TestStorage.Create();
        var world = await World.CreateAsync(storage, queueItemRemovedEnabled: false, orphanedDownloadRemovedEnabled: false);
        var path = Path.Combine(storage.DataRoot, "downloads", Release);
        Directory.CreateDirectory(path);
        Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromHours(3));
        var orphanedService = new OrphanedDownloadService(
            world.Libraries,
            world.Settings,
            world.Service,
            world.Jobs,
            world.Processors,
            world.Kept,
            world.Activity,
            TimeProvider.System,
            NullLogger<OrphanedDownloadService>.Instance,
            world.Outbound,
            world.Preferences,
            recycleBinService: new Deluno.Platform.RecycleBinService(Microsoft.Extensions.Options.Options.Create(new Deluno.Infrastructure.Storage.StoragePathOptions { DataRoot = storage.DataRoot }), TimeProvider.System, NullLogger<Deluno.Platform.RecycleBinService>.Instance));

        var action = await orphanedService.RemoveAsync(path, CancellationToken.None);

        Assert.True(action.Done, action.Message);
        Assert.DoesNotContain("download.orphan.removed", world.Outbound.DispatchedCategories);
    }

    /// <summary>
    /// #966: the stalled-download category (download.health.remediated,
    /// StalledDownloadHandledEnabled) already dispatched, but had no
    /// fires/suppressed test of its own.
    /// </summary>
    [Fact]
    public async Task A_stalled_download_dispatches_download_health_remediated_when_the_toggle_is_on()
    {
        using var storage = TestStorage.Create();
        var world = await World.CreateAsync(storage, queueItemRemovedEnabled: false, stalledDownloadHandledEnabled: true);

        var report = await world.RemediateStalledDownloadAsync();

        Assert.Equal(1, report.ReleasesRefused);
        Assert.Contains("download.health.remediated", world.Outbound.DispatchedCategories);
    }

    [Fact]
    public async Task A_stalled_download_stays_silent_when_the_toggle_is_off()
    {
        using var storage = TestStorage.Create();
        var world = await World.CreateAsync(storage, queueItemRemovedEnabled: false, stalledDownloadHandledEnabled: false);

        var report = await world.RemediateStalledDownloadAsync();

        Assert.Equal(1, report.ReleasesRefused);
        Assert.DoesNotContain("download.health.remediated", world.Outbound.DispatchedCategories);
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

    private sealed class FakeClient(string protocol) : IDownloadClient
    {
        public string Protocol => protocol;

        public DownloadClientTelemetryCapabilities Capabilities { get; } =
            new(true, true, true, true, true, true, protocol);

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
            => Task.FromResult(new DownloadClientActionResult(client.Id, queueItemId, action, true, $"{action} sent to {client.Name}"));

        public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>?>(null);

        public string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
            => nativeStatus ?? "unknown";
    }

    private sealed class PassThroughResiliencePolicy : IIntegrationResiliencePolicy
    {
        public async Task<IntegrationResilienceResult<T>> ExecuteAsync<T>(
            IntegrationResilienceRequest request,
            Func<CancellationToken, Task<T>> operation,
            Func<T, IntegrationResilienceOutcome> classifyResult,
            CancellationToken cancellationToken)
            => new(await operation(cancellationToken), false, false, 1, null, null);

        public bool IsCircuitOpen(string key, out DateTimeOffset retryAfterUtc)
        {
            retryAfterUtc = default;
            return false;
        }

        public Task CloseCircuitsAsync(string serviceType, string serviceId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class World
    {
        public required DownloadClientTelemetryService Service { get; init; }

        public required RecordingOutboundNotificationService Outbound { get; init; }

        public required INotificationService Preferences { get; init; }

        public required string ClientId { get; init; }

        public required string ClientName { get; init; }

        public required FixedTimeProvider Time { get; init; }

        public required ILibrariesRepository Libraries { get; init; }

        public required IPlatformSettingsRepository Settings { get; init; }

        public required IJobQueueRepository Jobs { get; init; }

        public required IActivityFeedRepository Activity { get; init; }

        public required IProcessorRepository Processors { get; init; }

        public required IKeptDownloadsRepository Kept { get; init; }

        public static async Task<World> CreateAsync(
            TestStorage storage,
            bool queueItemRemovedEnabled = true,
            bool orphanedDownloadRemovedEnabled = false,
            bool stalledDownloadHandledEnabled = false)
        {
            var now = DateTimeOffset.Parse("2026-09-27T09:00:00Z");
            var time = new FixedTimeProvider(now);
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            var settings = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            await AnInstallThatHasTheStrikeActionsOn.ApplyAsync(settings);
            var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var libraries = new SqliteLibrariesRepository(storage.Factory, time);
            var library = await libraries.CreateLibraryAsync(new CreateLibraryRequest(
                "Library", "movies", "library", Path.Combine(storage.DataRoot, "media"), Path.Combine(storage.DataRoot, "downloads"), null,
                "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
            var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
                "qbittorrent", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);

            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "entity-1", Release, Indexer, client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(dispatchId, QueueItemId, null, completed: false, CancellationToken.None);

            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var registry = new DownloadClientRegistry([new FakeClient("qbittorrent")]);
            var outbound = new RecordingOutboundNotificationService();
            var notificationService = new SqliteNotificationService(storage.Factory, time);
            var preferences = await notificationService.GetPreferencesAsync(CancellationToken.None);
            preferences.QueueItemRemovedEnabled = queueItemRemovedEnabled;
            preferences.OrphanedDownloadRemovedEnabled = orphanedDownloadRemovedEnabled;
            preferences.StalledDownloadHandledEnabled = stalledDownloadHandledEnabled;
            await notificationService.UpdatePreferencesAsync(preferences, CancellationToken.None);

            var processors = new SqliteProcessorRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var kept = new SqliteKeptDownloadsRepository(storage.Factory, time);

            var service = new DownloadClientTelemetryService(
                settings,
                new SqliteDownloadHealthRepository(storage.Factory, time),
                libraries,
                connections,
                jobs,
                registry,
                time,
                new PassThroughResiliencePolicy(),
                jobs,
                dispatches,
                jobs,
                new Deluno.Recovery.Policies.DownloadHealthEvaluator(),
                processors,
                Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance,
                blocklist,
                outboundNotificationService: outbound,
                notificationPreferences: notificationService);

            return new World
            {
                Service = service,
                Outbound = outbound,
                Preferences = notificationService,
                ClientId = client.Id,
                ClientName = client.Name,
                Time = time,
                Libraries = libraries,
                Settings = settings,
                Jobs = jobs,
                Activity = jobs,
                Processors = processors,
                Kept = kept
            };
        }

        /// <summary>
        /// #966: mirrors the stalled-download scenario in
        /// AStoppedDownloadIsRefusedOnTheBlocklistTests -- a download stalled
        /// past the strike threshold, so remediation refuses the release and
        /// (when the toggle is on) dispatches download.health.remediated.
        /// </summary>
        public Task<DownloadHealthRemediationReport> RemediateStalledDownloadAsync()
        {
            var finding = new DownloadHealthFinding("critical", DownloadHealthStrikes.ClientStalled, "Stopped", "Queue status: stalled.", "Review", false, false, StrikeCount: 3);
            var item = new DownloadQueueItem(
                QueueItemId, ClientId, ClientName, "qbittorrent", "movie", "Some Movie", Release, "movies",
                DownloadQueueStatuses.Stalled, 15, 0, 0, 1024, 128, 0, Indexer, null, Time.GetUtcNow(), HealthFindings: [finding]);
            var snapshot = new DownloadClientTelemetrySnapshot(
                ClientId, ClientName, "qbittorrent", null, "healthy", null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "none"),
                new DownloadTelemetrySummary(0, 0, 0, 1, 0, 0, 0), [item], [], Time.GetUtcNow());
            return Service.RunConfiguredHealthRemediationAsync(
                new DownloadTelemetryOverview(snapshot.Summary, [snapshot], Time.GetUtcNow()), CancellationToken.None);
        }
    }
}
