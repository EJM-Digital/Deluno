using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Recovery.Policies;

namespace Deluno.Persistence.Tests.Integrations;

public sealed class DownloadHealthGrabGuardTests
{
    /// <summary>
    /// Cleanup sheet J1/J2 (#924) dropped the "Allow removing client queue
    /// entries" setting that used to gate this: the Queue's remove dialog is
    /// now the confirmation, asking why in plain words, so removal is
    /// attempted directly rather than refused by a separate configuration
    /// gate. It still fails here, because nothing real is listening on
    /// "localhost:8080" - but for that reason, not the old setting.
    /// </summary>
    [Fact]
    public async Task External_client_removal_is_no_longer_gated_by_a_setting()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        await new PlatformSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var settingsRepository = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        await AnInstallThatHasTheStrikeActionsOn.ApplyAsync(settingsRepository);
        var healthRepository = new SqliteDownloadHealthRepository(storage.Factory, time);
        var connectionsRepository = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var client = await connectionsRepository.CreateDownloadClientAsync(new CreateDownloadClientRequest(
            "External qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
        var librariesRepository = new SqliteLibrariesRepository(storage.Factory, time);
        var service = new DownloadClientTelemetryService(
            settingsRepository, healthRepository, librariesRepository, connectionsRepository, null!,
            new DownloadClientRegistry([]), time, new PassThroughResiliencePolicy(), null!, new Deluno.Persistence.Tests.Support.NullDownloadDispatchesRepository(), null!,
            new DownloadHealthEvaluator(), null!, Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance);

        var result = await service.ExecuteActionAsync(
            client.Id,
            new DownloadClientActionRequest("delete", "external-queue-item"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain("Allow removing client queue entries", result.Message, StringComparison.Ordinal);
        // #1062: the only thing that stops removal now is ownership, never a setting.
        Assert.True(result.Refused);
        Assert.Contains("Deluno did not send", result.Message, StringComparison.Ordinal);
    }

    /// <summary>A resilience policy that just runs the operation once, for tests that do not care about retries.</summary>
    private sealed class PassThroughResiliencePolicy : Deluno.Infrastructure.Resilience.IIntegrationResiliencePolicy
    {
        public async Task<Deluno.Infrastructure.Resilience.IntegrationResilienceResult<T>> ExecuteAsync<T>(
            Deluno.Infrastructure.Resilience.IntegrationResilienceRequest request,
            Func<CancellationToken, Task<T>> operation,
            Func<T, Deluno.Infrastructure.Resilience.IntegrationResilienceOutcome> classifyResult,
            CancellationToken cancellationToken)
            => new(await operation(cancellationToken), false, false, 1, null, null);

        public bool IsCircuitOpen(string key, out DateTimeOffset retryAfterUtc)
        {
            retryAfterUtc = default;
            return false;
        }

        public Task CloseCircuitsAsync(string serviceType, string serviceId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task Health_policy_queues_one_exact_replacement_for_a_proven_dispatch()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var settingsRepository = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        await AnInstallThatHasTheStrikeActionsOn.ApplyAsync(settingsRepository);
        var healthRepository = new SqliteDownloadHealthRepository(storage.Factory, time);
        var connectionsRepository = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var librariesRepository = new SqliteLibrariesRepository(storage.Factory, time);
        var library = await librariesRepository.CreateLibraryAsync(new CreateLibraryRequest(
            "Movies", "movies", "library", Path.Combine(storage.DataRoot, "movies"), Path.Combine(storage.DataRoot, "downloads"), null,
            "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
        var client = await connectionsRepository.CreateDownloadClientAsync(new CreateDownloadClientRequest(
            "External qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "deluno-movies", "deluno-tv", null, 1, true), CancellationToken.None);
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
        var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
        var dispatchId = await jobs.RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "movie-arrival", "Arrival.2016.1080p.WEB", "Fixture", client.Id, client.Name, "sent", null,
            cancellationToken: CancellationToken.None);
        await dispatches.RecordDetectionAsync(dispatchId, "queue-arrival", 1024, completed: false, CancellationToken.None);

        // #904: CleanupRemoveClientEntryAfterThreshold now defaults to on, so
        // remediation reaches the registry even here; an empty one fails the
        // TryGet the same way a real registry would for an unregistered
        // protocol, rather than throwing on a null reference.
        var service = new DownloadClientTelemetryService(
            settingsRepository, healthRepository, librariesRepository, connectionsRepository, null!, new DownloadClientRegistry([]), time, null!, jobs, dispatches, jobs, new DownloadHealthEvaluator(), null!, Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance);
        var finding = new DownloadHealthFinding("critical", "client-stalled", "Stalled", "No progress", "Review", false, false, StrikeCount: 3);
        var item = new DownloadQueueItem("queue-arrival", client.Id, client.Name, client.Protocol, "movies", "Arrival", "Arrival.2016.1080p.WEB",
            "deluno-movies", DownloadQueueStatuses.Stalled, 15, 0, 0, 1024, 128, 0, "Fixture", null, time.GetUtcNow(), HealthFindings: [finding]);
        var snapshot = new DownloadClientTelemetrySnapshot(client.Id, client.Name, client.Protocol, null, "healthy", null,
            new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "none"),
            new DownloadTelemetrySummary(0, 0, 0, 1, 0, 0, 0), [item], [], time.GetUtcNow());

        var report = await service.RunConfiguredHealthRemediationAsync(
            new DownloadTelemetryOverview(snapshot.Summary, [snapshot], time.GetUtcNow()), CancellationToken.None);

        Assert.Equal(1, report.ReplacementSearchesQueued);
        Assert.Equal(0, report.ClientEntriesRemoved);
        var replacement = Assert.Single(await jobs.ListAsync(10, CancellationToken.None));
        Assert.Equal(LibrarySearchJobTypes.For("movies"), replacement.JobType);
        Assert.Contains("movie-arrival", replacement.PayloadJson, StringComparison.Ordinal);
        var updated = await dispatches.GetDispatchAsync(dispatchId, CancellationToken.None);
        Assert.Equal("health-remediation-applied", updated!.ImportFailureCode);
    }

    /// <summary>
    /// #905: a rule set to "instant" (StrikeLimit: 1) has to act the moment
    /// its own strike is recorded, even while the platform-wide fallback
    /// threshold (used only by findings outside the rule engine) is still
    /// its higher default. Remediation used to read the raw StrikeCount
    /// against that fallback for every finding, rule-engine or not, so an
    /// instant rule never fired until three sightings - the same wait as
    /// every other rule, whatever its own StrikeLimit said.
    /// </summary>
    [Fact]
    public async Task A_rule_set_to_instant_acts_on_its_own_first_strike_not_the_platform_wide_fallback()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var settingsRepository = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        await AnInstallThatHasTheStrikeActionsOn.ApplyAsync(settingsRepository);
        var healthRepository = new SqliteDownloadHealthRepository(storage.Factory, time);
        var connectionsRepository = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var librariesRepository = new SqliteLibrariesRepository(storage.Factory, time);
        var library = await librariesRepository.CreateLibraryAsync(new CreateLibraryRequest(
            "Movies", "movies", "library", Path.Combine(storage.DataRoot, "movies"), Path.Combine(storage.DataRoot, "downloads"), null,
            "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
        var client = await connectionsRepository.CreateDownloadClientAsync(new CreateDownloadClientRequest(
            "External qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "deluno-movies", "deluno-tv", null, 1, true), CancellationToken.None);
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
        var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
        var dispatchId = await jobs.RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "movie-arrival", "Arrival.2016.1080p.WEB", "Fixture", client.Id, client.Name, "sent", null,
            cancellationToken: CancellationToken.None);
        await dispatches.RecordDetectionAsync(dispatchId, "queue-arrival", 1024, completed: false, CancellationToken.None);

        var settings = await settingsRepository.GetAsync(CancellationToken.None);
        Assert.True(settings.DownloadHealthStrikeThreshold > 1, "This test only proves something if the platform-wide fallback is higher than the rule's own instant limit.");

        var service = new DownloadClientTelemetryService(
            settingsRepository, healthRepository, librariesRepository, connectionsRepository, null!, new DownloadClientRegistry([]), time, null!, jobs, dispatches, jobs, new DownloadHealthEvaluator(), null!, Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance);
        var finding = new DownloadHealthFinding(
            "warning", DownloadHealthStrikes.DeadTorrent, "No seeds or peers", "0 peers", "Review", true, false,
            StrikeCount: 1, RuleId: "instant-dead-torrent", RuleActionPreset: DownloadHealthActionPresets.RetryElsewhere, RuleStrikeLimit: 1);
        var item = new DownloadQueueItem("queue-arrival", client.Id, client.Name, client.Protocol, "movies", "Arrival", "Arrival.2016.1080p.WEB",
            "deluno-movies", DownloadQueueStatuses.Stalled, 15, 0, 0, 1024, 128, 0, "Fixture", null, time.GetUtcNow(), HealthFindings: [finding]);
        var snapshot = new DownloadClientTelemetrySnapshot(client.Id, client.Name, client.Protocol, null, "healthy", null,
            new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "none"),
            new DownloadTelemetrySummary(0, 0, 0, 1, 0, 0, 0), [item], [], time.GetUtcNow());

        var report = await service.RunConfiguredHealthRemediationAsync(
            new DownloadTelemetryOverview(snapshot.Summary, [snapshot], time.GetUtcNow()), CancellationToken.None);

        Assert.Equal(1, report.ReplacementSearchesQueued);
        var updated = await dispatches.GetDispatchAsync(dispatchId, CancellationToken.None);
        Assert.Equal("health-remediation-applied", updated!.ImportFailureCode);
    }

    /// <summary>
    /// #814: the one sandbox switch shared by the strike/blocklist executor
    /// and the seeding-cleanup executor. On, it must log what it would have
    /// done - distinctly from a real action - and make no real change: no
    /// replacement search queued, and the dispatch left free for a real pass
    /// to still act on later.
    /// </summary>
    [Fact]
    public async Task Dry_run_previews_a_struck_downloads_action_instead_of_taking_it()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var settingsRepository = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        await AnInstallThatHasTheStrikeActionsOn.ApplyAsync(settingsRepository);
        var healthRepository = new SqliteDownloadHealthRepository(storage.Factory, time);
        var connectionsRepository = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var librariesRepository = new SqliteLibrariesRepository(storage.Factory, time);
        var library = await librariesRepository.CreateLibraryAsync(new CreateLibraryRequest(
            "Movies", "movies", "library", Path.Combine(storage.DataRoot, "movies"), Path.Combine(storage.DataRoot, "downloads"), null,
            "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
        var client = await connectionsRepository.CreateDownloadClientAsync(new CreateDownloadClientRequest(
            "External qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "deluno-movies", "deluno-tv", null, 1, true), CancellationToken.None);
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
        var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
        var dispatchId = await jobs.RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "movie-arrival", "Arrival.2016.1080p.WEB", "Fixture", client.Id, client.Name, "sent", null,
            cancellationToken: CancellationToken.None);
        await dispatches.RecordDetectionAsync(dispatchId, "queue-arrival", 1024, completed: false, CancellationToken.None);

        var current = await settingsRepository.GetAsync(CancellationToken.None);
        Assert.False(current.DownloadHealthDryRunEnabled, "Dry run must default to off.");
        await settingsRepository.SaveAsync(
            PlatformSettingsPatchMerger.Apply(current, new PatchPlatformSettingsRequest(DownloadHealthDryRunEnabled: true)),
            CancellationToken.None);

        var service = new DownloadClientTelemetryService(
            settingsRepository, healthRepository, librariesRepository, connectionsRepository, null!, new DownloadClientRegistry([]), time, null!, jobs, dispatches, jobs, new DownloadHealthEvaluator(), null!, Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance);
        var finding = new DownloadHealthFinding("critical", "client-stalled", "Stalled", "No progress", "Review", false, false, StrikeCount: 3);
        var item = new DownloadQueueItem("queue-arrival", client.Id, client.Name, client.Protocol, "movies", "Arrival", "Arrival.2016.1080p.WEB",
            "deluno-movies", DownloadQueueStatuses.Stalled, 15, 0, 0, 1024, 128, 0, "Fixture", null, time.GetUtcNow(), HealthFindings: [finding]);
        var snapshot = new DownloadClientTelemetrySnapshot(client.Id, client.Name, client.Protocol, null, "healthy", null,
            new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "none"),
            new DownloadTelemetrySummary(0, 0, 0, 1, 0, 0, 0), [item], [], time.GetUtcNow());

        await service.RunConfiguredHealthRemediationAsync(
            new DownloadTelemetryOverview(snapshot.Summary, [snapshot], time.GetUtcNow()), CancellationToken.None);

        // No real job was queued and the dispatch was left free for a real
        // pass to still act on later - the report's count is a preview tally
        // ("would queue a replacement search"), not proof anything happened.
        Assert.Empty(await jobs.ListAsync(10, CancellationToken.None));
        var updated = await dispatches.GetDispatchAsync(dispatchId, CancellationToken.None);
        Assert.NotEqual("health-remediation-applied", updated!.ImportFailureCode);
        var activity = await jobs.ListActivityAsync(20, "download_dispatch", dispatchId, CancellationToken.None);
        var entry = Assert.Single(activity, entry => entry.Category == "download.health.remediation-previewed");
        Assert.StartsWith("[Dry run]", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Untested_external_client_is_refused_before_any_grab_or_network_call()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        await new PlatformSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var connectionsRepository = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var client = await connectionsRepository.CreateDownloadClientAsync(new CreateDownloadClientRequest(
            "Untested qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
        var service = new DownloadClientGrabService(
            connectionsRepository, null!, null!, null!, null!);

        var result = await service.GrabAsync(client.Id, new DownloadClientGrabRequest(
            "Example Movie 2026 1080p", "https://fixture.invalid/release", "movies", "movies", "Fixture source"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("unready", result.Status);
        Assert.Contains("connection test", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
