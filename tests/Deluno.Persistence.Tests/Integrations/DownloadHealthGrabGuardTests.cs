using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Realtime;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;

namespace Deluno.Persistence.Tests.Integrations;

public sealed class DownloadHealthGrabGuardTests
{
    [Fact]
    public async Task External_client_removal_is_refused_until_the_manual_queue_removal_setting_is_enabled()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        await new PlatformSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var settingsRepository = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var healthRepository = new SqliteDownloadHealthRepository(storage.Factory, time);
        var connectionsRepository = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var client = await connectionsRepository.CreateDownloadClientAsync(new CreateDownloadClientRequest(
            "External qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
        var librariesRepository = new SqliteLibrariesRepository(storage.Factory, time);
        var service = new DownloadClientTelemetryService(
            settingsRepository, healthRepository, librariesRepository, connectionsRepository, null!, null!, time, null!, null!, null!, null!);

        var result = await service.ExecuteActionAsync(
            client.Id,
            new DownloadClientActionRequest("delete", "external-queue-item"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("queue removal is disabled", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Repeatedly_unhealthy_exact_release_is_refused_before_a_client_grab()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-13T00:00:00Z"));
        await new PlatformSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var healthRepository = new SqliteDownloadHealthRepository(storage.Factory, time);
        var connectionsRepository = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var client = await connectionsRepository.CreateDownloadClientAsync(new CreateDownloadClientRequest(
            "External qBittorrent", "qbittorrent", "localhost", 8080, null, null, "C:\\Downloads", "movies", "tv", null, 1, true), CancellationToken.None);
        var observation = new DownloadHealthObservation(client.Id, "queue-1", "Example.Movie.2026.1080p", "client-stalled", "critical", "Client failure.");

        await healthRepository.RecordDownloadHealthObservationsAsync([observation], CancellationToken.None);
        time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-13T00:31:00Z"));
        healthRepository = new SqliteDownloadHealthRepository(storage.Factory, time);
        await healthRepository.RecordDownloadHealthObservationsAsync([observation], CancellationToken.None);
        time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-13T01:02:00Z"));
        healthRepository = new SqliteDownloadHealthRepository(storage.Factory, time);
        await healthRepository.RecordDownloadHealthObservationsAsync([observation], CancellationToken.None);

        var service = new DownloadClientGrabService(
            healthRepository, connectionsRepository, null!, null!, null!, null!, null!, null!, time);
        var result = await service.GrabAsync(client.Id, new DownloadClientGrabRequest(
            "Example Movie 2026 1080p", "https://fixture.invalid/release", "movies", "movies", "Fixture source"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("blocked", result.Status);
        Assert.Contains("repeatedly failed", result.Message, StringComparison.OrdinalIgnoreCase);
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
        await dispatches.RecordDetectionAsync(dispatchId, "queue-arrival", 1024, CancellationToken.None);

        var service = new DownloadClientTelemetryService(
            settingsRepository, healthRepository, librariesRepository, connectionsRepository, null!, null!, time, null!, jobs, dispatches, jobs);
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
        Assert.Equal("library.search", replacement.JobType);
        Assert.Contains("movie-arrival", replacement.PayloadJson, StringComparison.Ordinal);
        var updated = await dispatches.GetDispatchAsync(dispatchId, CancellationToken.None);
        Assert.Equal("health-remediation-applied", updated!.ImportFailureCode);
    }

    [Fact]
    public async Task Untested_external_client_is_refused_before_any_grab_or_network_call()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        await new PlatformSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, time), NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var healthRepository = new SqliteDownloadHealthRepository(storage.Factory, time);
        var connectionsRepository = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var client = await connectionsRepository.CreateDownloadClientAsync(new CreateDownloadClientRequest(
            "Untested qBittorrent", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
        var service = new DownloadClientGrabService(
            healthRepository, connectionsRepository, null!, null!, null!, null!, null!, null!, time);

        var result = await service.GrabAsync(client.Id, new DownloadClientGrabRequest(
            "Example Movie 2026 1080p", "https://fixture.invalid/release", "movies", "movies", "Fixture source"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("unready", result.Status);
        Assert.Contains("connection test", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
