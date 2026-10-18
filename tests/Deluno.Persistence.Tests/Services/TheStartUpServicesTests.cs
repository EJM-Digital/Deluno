using System.Reflection;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Search;
using Deluno.Integrations.Search.Trackers;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.RealDisk;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Quality.Guides;
using Deluno.Recovery.Contracts;
using Deluno.Recovery.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Services;

/// <summary>
/// The services that do their work once at start-up or on a schedule, started the way the host starts them: each one does
/// what its description says, writes the outcome where a screen can read it, and a failure is logged without taking the
/// host down.
/// </summary>
public sealed class TheStartUpServicesTests
{
    private const string Release = "Wanted.Film.2011.1080p.WEB-DL-DELUNO";

    // ---------------------------------------------------------------- IndexerIdentityBackfillService

    [Fact]
    public async Task At_start_older_history_filed_under_an_indexers_name_is_linked_to_that_indexer_and_a_stranger_is_left_alone()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var connections = app.Services.GetRequiredService<IConnectionsRepository>();
        var queue = app.Services.GetRequiredService<IJobQueueRepository>();
        var library = await app.Services.GetRequiredService<ILibrariesRepository>().CreateLibraryAsync(
            new CreateLibraryRequest(
                "Films", "movies", "library", Path.Combine(app.DataRoot, "movies"), Path.Combine(app.DataRoot, "downloads"), null,
                "direct-import", null, null, null, null, true, true, true, 12, 36, 25),
            CancellationToken.None);

        // Written before any indexer of that name exists, as a row from before ids were kept is.
        await queue.RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "title-one", Release, "Later indexer", "client-1", "qBittorrent", "sent", null,
            cancellationToken: CancellationToken.None);
        await queue.RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "title-two", "Other.Film.2013.1080p.WEB-DL-DELUNO", "Stranger", "client-1", "qBittorrent", "sent", null,
            cancellationToken: CancellationToken.None);
        var indexer = await connections.CreateIndexerAsync(
            new CreateIndexerRequest("Later indexer", "torznab", "public", "http://indexer.invalid/api", null, 25, "2000", "", "both", IsEnabled: true),
            CancellationToken.None);
        await LegacyConnectionNames.SetAsync(app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>(), "indexer_sources", indexer.Id, "Later indexer");

        var service = new IndexerIdentityBackfillService(
            app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>(),
            NullLogger<IndexerIdentityBackfillService>.Instance);
        await service.StartAsync(CancellationToken.None);

        var one = await queue.FindDispatchLinkForDownloadAsync("client-1", "no-hash", Release, CancellationToken.None);
        var two = await queue.FindDispatchLinkForDownloadAsync("client-1", "no-hash", "Other.Film.2013.1080p.WEB-DL-DELUNO", CancellationToken.None);
        Assert.Equal(indexer.Id, one!.IndexerId);
        Assert.Null(two!.IndexerId);
    }

    [Fact]
    public async Task A_start_whose_linking_breaks_is_logged_and_does_not_stop_the_host()
    {
        var logger = new TheWorkersOnRealFilesTests.CapturingLogger<IndexerIdentityBackfillService>();
        var service = new IndexerIdentityBackfillService(Throwing.Create<IDelunoDatabaseConnectionFactory>(new InvalidOperationException("the database is locked")), logger);

        await service.StartAsync(CancellationToken.None);

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("stays filed by name"));
    }

    // ---------------------------------------------------------------- IndexerNamesUpgradeService

    [Fact]
    public async Task At_start_an_indexer_added_from_a_tracker_definition_takes_its_automatic_name_and_a_second_start_changes_nothing()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-16T00:00:00Z"));
        await new PlatformSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, clock), NullLogger<PlatformSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var repository = new SqliteConnectionsRepository(storage.Factory, clock, TestSecretProtection.Create(storage), new OnlyThe1337xDefinition());
        var tracker = await repository.CreateIndexerAsync(
            new CreateIndexerRequest(null, "newznab", "public", "https://1337x.to", "key", 10, "2000,5000", null, "both", true) { DefinitionId = "1337x" },
            CancellationToken.None);
        await LegacyConnectionNames.SetAsync(storage.Factory, "indexer_sources", tracker.Id, "My typed tracker");
        var service = new IndexerNamesUpgradeService(repository, NullLogger<IndexerNamesUpgradeService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal("1337x", (await repository.ListIndexersAsync(CancellationToken.None)).Single().Name);

        await LegacyConnectionNames.SetAsync(storage.Factory, "indexer_sources", tracker.Id, "Renamed by hand after the upgrade");
        await service.StartAsync(CancellationToken.None);
        Assert.Equal("1337x", (await repository.ListIndexersAsync(CancellationToken.None)).Single().Name);
    }

    [Fact]
    public async Task A_start_whose_renaming_breaks_is_logged_and_the_indexers_keep_their_names()
    {
        var logger = new TheWorkersOnRealFilesTests.CapturingLogger<IndexerNamesUpgradeService>();
        var service = new IndexerNamesUpgradeService(Throwing.Create<IConnectionsRepository>(new InvalidOperationException("the database is locked")), logger);

        await service.StartAsync(CancellationToken.None);

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("keep the names they have"));
    }

    // ---------------------------------------------------------------- GuideCatalogueUpgradeHostedService

    [Fact]
    public async Task At_start_a_stored_guide_package_from_before_genuine_formats_is_stored_again_with_the_new_layer_and_the_old_version_stays()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-16T00:00:00Z"));
        await new PlatformSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, clock), NullLogger<PlatformSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var store = new SqliteGuidePackageStore(storage.Factory, clock);
        var shipped = GuidePackageCatalog.Current;
        var older = shipped with { SchemaVersion = 2, Version = shipped.Version + 3, IntegritySha256 = null };
        var stored = await store.ApplyAsync(new GuidePackageUpdateRequest(older, null), CancellationToken.None);
        var service = new GuideCatalogueUpgradeHostedService(store, ReadyGate.Create(), NullLogger<GuideCatalogueUpgradeHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(async () => (await store.GetCurrentAsync(CancellationToken.None)).Package.SchemaVersion == shipped.SchemaVersion, "the stored package to carry the new layer");
        await service.StopAsync(CancellationToken.None);

        var current = await store.GetCurrentAsync(CancellationToken.None);
        Assert.Equal(stored.Package.Version + 1, current.Package.Version);
        Assert.Contains(await store.ListAsync(CancellationToken.None), item => item.Package.Version == stored.Package.Version && !item.IsActive);
    }

    [Fact]
    public async Task A_guide_upgrade_that_breaks_is_logged_and_the_stored_package_stays_active()
    {
        var logger = new TheWorkersOnRealFilesTests.CapturingLogger<GuideCatalogueUpgradeHostedService>();
        var service = new GuideCatalogueUpgradeHostedService(Throwing.Create<IGuidePackageStore>(new InvalidOperationException("the database is locked")), ReadyGate.Create(), logger);

        await service.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => logger.Entries.Any(entry => entry.Level == LogLevel.Warning), "the failed upgrade to be logged");
        await service.StopAsync(CancellationToken.None);

        Assert.Contains(logger.Entries, entry => entry.Message.Contains("the stored package stays active"));
    }

    // ---------------------------------------------------------------- TrackerDefinitionAutoUpdateHostedService

    [Fact]
    public async Task Once_the_databases_are_ready_the_tracker_definition_update_is_asked_whether_it_is_due_and_a_failure_is_only_logged()
    {
        var updates = new CountingTrackerUpdates { Fail = true };
        var logger = new TheWorkersOnRealFilesTests.CapturingLogger<TrackerDefinitionAutoUpdateHostedService>();
        var service = new TrackerDefinitionAutoUpdateHostedService(updates, ReadyGate.Create(), logger);

        await service.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => updates.DueChecks >= 1, "the first due check");
        await Wait.UntilAsync(() => logger.Entries.Any(entry => entry.Level == LogLevel.Warning), "the failed check to be logged");
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, updates.DueChecks);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("could not run"));
    }

    [Fact]
    public async Task The_tracker_definition_update_does_not_look_before_the_databases_are_ready()
    {
        var updates = new CountingTrackerUpdates();
        var service = new TrackerDefinitionAutoUpdateHostedService(updates, new DelunoStartupGate(), NullLogger<TrackerDefinitionAutoUpdateHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(100); // reason: shows nothing happens while the gate stays shut; there is no event to wait for
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, updates.DueChecks);
    }

    // ---------------------------------------------------------------- RankingModelTrainingHostedService

    [Fact]
    public async Task At_start_the_ranking_model_is_trained_and_the_run_is_recorded_as_completed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var trainer = new ScriptedTrainer(success: true);
        var service = Service(trainer, jobs, enabled: true);

        await service.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(async () => (await StateAsync(jobs)) is { LastResult: not null }, "the training run to be recorded");
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(["startup"], trainer.Reasons);
        Assert.Equal("completed", (await StateAsync(jobs))!.LastResult);
    }

    [Fact]
    public async Task A_training_run_that_finds_too_little_to_learn_from_is_recorded_as_skipped_not_completed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var service = Service(new ScriptedTrainer(success: false), jobs, enabled: true);

        await service.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(async () => (await StateAsync(jobs)) is { LastResult: not null }, "the training run to be recorded");
        await service.StopAsync(CancellationToken.None);

        Assert.Equal("skipped", (await StateAsync(jobs))!.LastResult);
    }

    [Fact]
    public async Task While_the_ranking_model_is_switched_off_nothing_is_trained_and_no_run_is_recorded()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var trainer = new ScriptedTrainer(success: true);
        var service = Service(trainer, jobs, enabled: false);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(trainer.Reasons);
        Assert.Null(await StateAsync(jobs));
    }

    [Fact]
    public async Task With_training_at_start_switched_off_the_first_run_waits_for_its_schedule()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var trainer = new ScriptedTrainer(success: true);
        var service = Service(trainer, jobs, enabled: true, trainOnStartup: false);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(150); // reason: shows no run happens at start; nothing signals the absence of one
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(trainer.Reasons);
    }

    private static RankingModelTrainingHostedService Service(ScriptedTrainer trainer, IJobQueueRepository jobs, bool enabled, bool trainOnStartup = true)
        => new(
            Throwing.Create<IReleaseRankingModelService, RankingModelStatus>(
                nameof(IReleaseRankingModelService.GetStatus),
                new RankingModelStatus(enabled, false, 5, "assist", "")),
            trainer,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Deluno:RankingModel:TrainOnStartup"] = trainOnStartup.ToString() }).Build(),
            NullLogger<RankingModelTrainingHostedService>.Instance,
            jobs,
            TimeProvider.System,
            ReadyGate.Create());

    private static async Task<SystemTaskState?> StateAsync(IJobQueueRepository jobs)
        => (await jobs.ListSystemTaskStatesAsync(CancellationToken.None)).SingleOrDefault(item => item.ScheduleKey == SystemTasks.RankingModelTraining);

    // ---------------------------------------------------------------- ImportRecoveryRetentionService

    [Fact]
    public async Task A_recovery_clean_up_that_fails_is_recorded_failed_and_logged_and_the_service_keeps_running()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var jobs = services.GetRequiredService<IJobQueueRepository>();
        var logger = new TheWorkersOnRealFilesTests.CapturingLogger<ImportRecoveryRetentionService>();
        var service = new ImportRecoveryRetentionService(
            Throwing.Create<IMovieImportRecoveryRetentionRepository>(new InvalidOperationException("the database is locked")),
            services.GetRequiredService<ISeriesImportRecoveryRetentionRepository>(),
            services.GetRequiredService<IPlatformSettingsRepository>(),
            TimeProvider.System,
            logger,
            jobs);

        await service.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(async () => (await jobs.ListSystemTaskStatesAsync(CancellationToken.None)).Any(state => state.ScheduleKey == SystemTasks.ImportRecoveryRetention && state.LastResult is not null), "the failed clean-up to be recorded");
        Assert.False(service.ExecuteTask!.IsCompleted, "a failed clean-up must not end the daily loop");
        await service.StopAsync(CancellationToken.None);

        var state = (await jobs.ListSystemTaskStatesAsync(CancellationToken.None)).Single(item => item.ScheduleKey == SystemTasks.ImportRecoveryRetention);
        Assert.Equal("failed", state.LastResult);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Import recovery cleanup encountered an error"));
    }

    // ---------------------------------------------------------------- helpers

    private sealed class OnlyThe1337xDefinition : ITrackerDefinitionNames
    {
        public string? DisplayName(string definitionId) => definitionId == "1337x" ? "1337x" : null;
    }

    private sealed class CountingTrackerUpdates : ITrackerDefinitionUpdateService
    {
        private int _dueChecks;

        public bool Fail { get; init; }

        public int DueChecks => Volatile.Read(ref _dueChecks);

        public Task<TrackerDefinitionUpdateState> RunIfDueAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _dueChecks);
            return Fail ? throw new InvalidOperationException("the definitions could not be fetched") : Task.FromResult<TrackerDefinitionUpdateState>(null!);
        }

        public Task<TrackerDefinitionUpdateState> GetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TrackerDefinitionUpdateState> SetSettingsAsync(bool isEnabled, int intervalHours, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TrackerDefinitionUpdateState> UpdateNowAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ScriptedTrainer(bool success) : IReleaseRankingModelAdminService
    {
        public List<string> Reasons { get; } = [];

        public Task<RankingModelTrainingResult> TrainAsync(string reason, CancellationToken cancellationToken)
        {
            Reasons.Add(reason);
            return Task.FromResult(new RankingModelTrainingResult(success, success ? "Trained." : "Too few samples.", success ? "v1" : null, success ? 200 : 3, null, null, DateTimeOffset.UtcNow));
        }

        public bool TryRollback(string version, out string message)
        {
            message = "";
            return false;
        }
    }
}

/// <summary>A start-up gate that is already open, as it is once every database has its schema.</summary>
internal static class ReadyGate
{
    public static IDelunoStartupGate Create()
    {
        var gate = new DelunoStartupGate();
        foreach (var database in DelunoStorageLayout.Databases)
        {
            gate.MarkReady(database.Key);
        }

        return gate;
    }
}

/// <summary>An implementation of an interface whose every call fails the same way, for the tests of what a service does when its store breaks.</summary>
internal class Throwing : DispatchProxy
{
    private Exception _exception = new InvalidOperationException();
    private readonly Dictionary<string, object?> _answers = new();

    public static T Create<T>(Exception exception)
        where T : class
    {
        var proxy = Create<T, Throwing>();
        ((Throwing)(object)proxy)._exception = exception;
        return proxy;
    }

    /// <summary>One method answers with a value; any other call fails.</summary>
    public static T Create<T, TAnswer>(string method, TAnswer answer)
        where T : class
    {
        var proxy = Create<T, Throwing>();
        ((Throwing)(object)proxy)._answers[method] = answer;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod is not null && _answers.TryGetValue(targetMethod.Name, out var answer) ? answer : throw _exception;
}
