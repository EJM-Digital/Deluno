using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Platform.Data;
using Deluno.Recovery.Services;
using Deluno.Worker.HealthChecks.Checks;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// The background workers run by the real code on a real host: a real temp data folder, real SQLite job store and real files.
/// The clock is a fake one the test moves, so "every five minutes" and "once an hour" are exercised without waiting.
/// </summary>
public sealed class TheWorkersOnRealFilesTests
{
    // ------------------------------------------------------------------ start-up recovery

    private static readonly byte[] Content = Enumerable.Range(0, 4096).Select(value => (byte)((value * 7) % 251)).ToArray();

    /// <summary>Runs the real recovery and records how many lane heartbeats had been written when it was called.</summary>
    private sealed class ObservedRecovery(ImportRestartRecoveryService real, IServiceProvider services, List<long> heartbeatsSeen) : IImportRestartRecoveryService
    {
        public async Task<ImportRestartRecoveryReport> RecoverAsync(CancellationToken cancellationToken)
        {
            var factory = services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>();
            await using (var connection = await factory.OpenConnectionAsync("jobs", cancellationToken))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM worker_heartbeats;";
                heartbeatsSeen.Add(Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)));
            }

            return await real.RecoverAsync(cancellationToken);
        }
    }

    private sealed class RecoveryThatThrows : IImportRestartRecoveryService
    {
        public Task<ImportRestartRecoveryReport> RecoverAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("the recovery could not read the library folders");
    }

    private static async Task<(RealDiskLibrary World, List<long> HeartbeatsSeen)> StartWithRecoveryAsync(bool recoveryThrows = false)
    {
        var heartbeatsSeen = new List<long>();
        var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            RealDiskLibrary.UseProbe(new ScriptedProbe(_ => ScriptedProbe.Good()))(services);
            services.AddScoped<ImportRestartRecoveryService>(provider => ActivatorUtilities.CreateInstance<ImportRestartRecoveryService>(provider));
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(IImportRestartRecoveryService)) services.RemoveAt(i);
            }

            if (recoveryThrows) services.AddScoped<IImportRestartRecoveryService, RecoveryThatThrows>();
            else services.AddScoped<IImportRestartRecoveryService>(provider => new ObservedRecovery(provider.GetRequiredService<ImportRestartRecoveryService>(), provider, heartbeatsSeen));
        });
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads"));
        return (await RealDiskLibrary.OpenAsync(app), heartbeatsSeen);
    }

    /// <summary>What a crash leaves for one film: its staged file (the only copy, after a move) and a restart marker beside it.</summary>
    private static (string Staging, string Destination, string Marker) LayStagedMove(RealDiskLibrary world, string title, string? markerText)
    {
        var folder = Path.Combine(world.Root, $"{title} (2024)");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, $"{title} (2024).mkv");
        var staging = destination + ".deluno-stage-startuptest.tmp";
        File.WriteAllBytes(staging, Content);
        File.SetLastWriteTimeUtc(staging, DateTime.UtcNow.AddMinutes(-5));
        var marker = ImportRestartMarkerFile.PathFor(staging);
        if (markerText is null)
        {
            var source = Path.Combine(world.Downloads, $"{title}.2024.WEB.1080p.mkv");
            Assert.Null(ImportRestartMarkerFile.WriteAtomic(marker, new ImportRestartMarker(source, destination, staging, "movies", $"{title} (2024)")));
        }
        else
        {
            File.WriteAllText(marker, markerText);
        }

        return (staging, destination, marker);
    }

    private static DelunoHeartbeatWorker WorkerFor(ApplicationTestHost app, ILogger<DelunoHeartbeatWorker>? logger = null)
        => new(
            logger ?? NullLogger<DelunoHeartbeatWorker>.Instance,
            app.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            app.Services.GetRequiredService<IJobLaneSignal>(),
            new JobLaneStateBoard(TimeProvider.System));

    private static async Task<long> HeartbeatRowsAsync(RealDiskLibrary world)
        => Convert.ToInt64(await world.ScalarAsync("jobs", "SELECT COUNT(*) FROM worker_heartbeats;"));

    [Fact]
    public async Task At_start_up_the_worker_finishes_an_interrupted_import_and_keeps_a_damaged_markers_media_before_any_lane_has_ticked()
    {
        var (world, heartbeatsSeen) = await StartWithRecoveryAsync();
        await using var _ = world;
        var good = LayStagedMove(world, "Sanctuary", markerText: null);
        var damaged = LayStagedMove(world, "Nightfall", markerText: "{\"SourcePath\":\"C:\\\\downloads\\\\Night");
        var worker = WorkerFor(world.App);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Wait.UntilAsync(async () => await HeartbeatRowsAsync(world) > 0, "the first lane to tick and write its heartbeat");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        // The recovery ran exactly once, and no lane had written a heartbeat (let alone leased a job) when it did.
        Assert.Equal(0L, Assert.Single(heartbeatsSeen));
        var imported = Assert.Single(RealDiskLibrary.FilesUnder(world.Root), path => path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Content, File.ReadAllBytes(imported));
        Assert.False(File.Exists(good.Staging));
        Assert.False(File.Exists(good.Marker));
        var kept = Assert.Single(await world.Bin.ListAsync(CancellationToken.None));
        Assert.Equal(damaged.Staging, kept.OriginalPath);
        Assert.Equal(Content, File.ReadAllBytes(kept.RecyclePath));
        Assert.False(File.Exists(damaged.Destination), "a damaged marker never puts a half-known file into the library");
        var activity = await world.App.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(100, null, null, CancellationToken.None);
        Assert.Contains(activity, entry => entry.Category == "filesystem.import.restart-recovered");
        Assert.Contains(activity, entry => entry.Category == "filesystem.import.restart-media-kept");
    }

    [Fact]
    public async Task A_start_up_recovery_that_throws_is_logged_and_the_worker_still_starts_its_lanes()
    {
        var (world, _) = await StartWithRecoveryAsync(recoveryThrows: true);
        await using var _ = world;
        var logger = new CapturingLogger<DelunoHeartbeatWorker>();
        var worker = WorkerFor(world.App, logger);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Wait.UntilAsync(async () => await HeartbeatRowsAsync(world) > 0, "the lanes to start and write their heartbeat although the recovery failed");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Import restart recovery failed to run at startup"));
        Assert.Contains("could not read the library folders", warning.Exception!.Message);
    }

    // ------------------------------------------------------------------ the scheduled passes of the work planner

    private static async Task<(ApplicationTestHost App, WorkPlanner Planner, FakeTimeProvider Clock)> StartPlannerAsync()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<TimeProvider>(clock));
        var planner = new WorkPlanner(
            NullLogger<WorkPlanner>.Instance,
            app.Services.GetRequiredService<IJobQueueRepository>(),
            new ConfigurationBuilder().Build(),
            clock);
        return (app, planner, clock);
    }

    private static async Task<SystemTaskState> StateAsync(ApplicationTestHost app, string key)
        => (await app.Services.GetRequiredService<IJobQueueRepository>().ListSystemTaskStatesAsync(CancellationToken.None)).Single(state => state.ScheduleKey == key);

    /// <summary>What the pass last recorded, or null while it has not recorded anything yet.</summary>
    private static async Task<string?> ResultAsync(ApplicationTestHost app, string key)
        => (await app.Services.GetRequiredService<IJobQueueRepository>().ListSystemTaskStatesAsync(CancellationToken.None)).SingleOrDefault(state => state.ScheduleKey == key)?.LastResult;

    private static async Task<string> AddOldLeftoverAsync(ApplicationTestHost app, string name, int ageHours)
    {
        var downloads = Path.Combine(app.DataRoot, "downloads");
        Directory.CreateDirectory(downloads);
        var folder = Path.Combine(downloads, name);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "movie.mkv");
        await File.WriteAllTextAsync(file, "the film");
        var then = DateTime.UtcNow.AddHours(-ageHours);
        File.SetLastWriteTimeUtc(file, then);
        Directory.SetLastWriteTimeUtc(folder, then);
        await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            "lib-1", "movies", "movie", Guid.NewGuid().ToString("N"), name, "Indexer", "sabnzbd", "SABnzbd", "queued", null,
            cancellationToken: CancellationToken.None);
        return folder;
    }

    [Fact]
    public async Task The_leftover_download_pass_deletes_an_old_leftover_nothing_uses_and_says_why_in_Activity_while_a_young_one_stays()
    {
        var (app, planner, _) = await StartPlannerAsync();
        await using var _ = app;
        var downloads = Path.Combine(app.DataRoot, "downloads");
        Directory.CreateDirectory(downloads);
        Directory.CreateDirectory(Path.Combine(app.DataRoot, "movies"));
        var created = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name = "Movies", mediaType = "movies", purpose = "collection", rootPath = Path.Combine(app.DataRoot, "movies"), downloadsPath = downloads,
            qualityProfileId = (string?)null, importWorkflow = "copy", processorName = (string?)null, processorOutputPath = (string?)null,
            processorTimeoutMinutes = (int?)null, processorFailureMode = (string?)null, autoSearchEnabled = false, missingSearchEnabled = false,
            upgradeSearchEnabled = false, searchIntervalMinutes = (int?)null, retryDelayHours = (int?)null, maxItemsPerRun = (int?)null
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var old = await AddOldLeftoverAsync(app, "Old.Movie.2019.1080p.WEB", ageHours: 48);
        var young = await AddOldLeftoverAsync(app, "Young.Movie.2024.1080p.WEB", ageHours: 5);
        (await app.Client.PatchAsJsonAsync("/api/settings", new { leftoverDownloadCleanupEnabled = true, leftoverDownloadCleanupAgeHours = 24 })).EnsureSuccessStatusCode();
        var service = app.Services.GetRequiredService<ILeftoverDownloadCleanupService>();

        await planner.RunLeftoverDownloadCleanupAsync(service, CancellationToken.None);

        Assert.False(Directory.Exists(old), "the leftover older than the threshold is deleted from the real disk");
        Assert.True(File.Exists(Path.Combine(young, "movie.mkv")), "a leftover younger than the threshold stays");
        var activity = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(100, null, null, CancellationToken.None);
        Assert.Contains(activity, entry => entry.Message.Contains("Old.Movie.2019.1080p.WEB") && entry.Message.Contains("nothing is using it any more"));
        Assert.Equal("completed", (await StateAsync(app, SystemTasks.DownloadLeftoverCleanup)).LastResult);
    }

    [Fact]
    public async Task The_leftover_download_pass_deletes_nothing_while_its_switch_is_off()
    {
        var (app, planner, _) = await StartPlannerAsync();
        await using var _ = app;
        var old = await AddOldLeftoverAsync(app, "Old.Movie.2019.1080p.WEB", ageHours: 96);

        await planner.RunLeftoverDownloadCleanupAsync(app.Services.GetRequiredService<ILeftoverDownloadCleanupService>(), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(old, "movie.mkv")), "the switch is off by default, so the pass removes nothing");
        Assert.Equal("completed", (await StateAsync(app, SystemTasks.DownloadLeftoverCleanup)).LastResult);
    }

    private sealed class ConnectivityCheck(Func<Task<bool>> answer) : IDownloadHealthConnectivityCheck
    {
        public int Calls { get; private set; }

        public Task<bool> AnyReachableAsync(string commaSeparatedUrls, CancellationToken cancellationToken)
        {
            Calls++;
            return answer();
        }
    }

    private static Task ReclaimAsync(ApplicationTestHost app, WorkPlanner planner, IDownloadHealthConnectivityCheck connectivity, CancellationToken cancellationToken)
    {
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        return planner.PlanSharingReclaimAsync(
            services.GetRequiredService<IDownloadClientTelemetryService>(),
            services.GetRequiredService<Deluno.Platform.Data.IPlatformSettingsRepository>(),
            services.GetRequiredService<Deluno.Connections.Data.IConnectionsRepository>(),
            services.GetRequiredService<Deluno.Libraries.Data.ILibrariesRepository>(),
            services.GetRequiredService<IActivityFeedRepository>(),
            services.GetRequiredService<IDownloadSharingRepository>(),
            services.GetRequiredService<Deluno.Movies.Data.IMovieCatalogRepository>(),
            services.GetRequiredService<Deluno.Series.Data.ISeriesCatalogRepository>(),
            services.GetRequiredService<SharingReclaimService>(),
            services.GetRequiredService<IHardlinkInspector>(),
            services.GetRequiredService<IDownloadHealthRepository>(),
            connectivity,
            cancellationToken,
            services.GetRequiredService<ISharingRulesRepository>(),
            services.GetRequiredService<ISharingActivityTracker>(),
            services.GetRequiredService<Deluno.Platform.Data.IProcessorRepository>());
    }

    [Fact]
    public async Task A_sharing_reclaim_pass_whose_connectivity_check_breaks_is_recorded_failed_and_tried_again_after_its_interval()
    {
        var (app, planner, clock) = await StartPlannerAsync();
        await using var _ = app;
        (await app.Client.PatchAsJsonAsync("/api/settings", new { downloadHealthConnectivityCheckEnabled = true })).EnsureSuccessStatusCode();
        var broken = true;
        var connectivity = new ConnectivityCheck(() => broken ? throw new InvalidOperationException("the check blew up") : Task.FromResult(true));

        await ReclaimAsync(app, planner, connectivity, CancellationToken.None);
        var failed = await StateAsync(app, SystemTasks.SharingReclaim);
        Assert.Equal("failed", failed.LastResult);
        Assert.False(string.IsNullOrWhiteSpace(failed.LastError));

        // Inside the interval nothing is retried; once it has passed the pass runs again and this time works.
        await ReclaimAsync(app, planner, connectivity, CancellationToken.None);
        Assert.Equal(1, connectivity.Calls);
        clock.Advance(SystemTasks.IntervalFor(SystemTasks.SharingReclaim) + TimeSpan.FromSeconds(1));
        broken = false;
        await ReclaimAsync(app, planner, connectivity, CancellationToken.None);

        Assert.Equal(2, connectivity.Calls);
        Assert.Equal("completed", (await StateAsync(app, SystemTasks.SharingReclaim)).LastResult);
    }

    [Fact]
    public async Task A_sharing_reclaim_pass_stopped_by_shutdown_is_recorded_cancelled_not_failed()
    {
        var (app, planner, _) = await StartPlannerAsync();
        await using var _ = app;
        (await app.Client.PatchAsJsonAsync("/api/settings", new { downloadHealthConnectivityCheckEnabled = true })).EnsureSuccessStatusCode();
        using var stopping = new CancellationTokenSource();
        var connectivity = new ConnectivityCheck(() =>
        {
            stopping.Cancel();
            throw new OperationCanceledException(stopping.Token);
        });

        await ReclaimAsync(app, planner, connectivity, stopping.Token);

        Assert.Equal("cancelled", (await StateAsync(app, SystemTasks.SharingReclaim)).LastResult);
    }

    [Fact]
    public async Task A_sharing_reclaim_pass_with_no_reachable_url_does_nothing_and_says_so_in_Activity()
    {
        var (app, planner, _) = await StartPlannerAsync();
        await using var _ = app;
        (await app.Client.PatchAsJsonAsync("/api/settings", new { downloadHealthConnectivityCheckEnabled = true })).EnsureSuccessStatusCode();
        var connectivity = new ConnectivityCheck(() => Task.FromResult(false));

        await ReclaimAsync(app, planner, connectivity, CancellationToken.None);

        Assert.Equal(1, connectivity.Calls);
        var activity = await app.Services.GetRequiredService<IActivityFeedRepository>().ListActivityAsync(100, null, null, CancellationToken.None);
        Assert.Contains(activity, entry => entry.Category == "download.health.connectivity-check-skipped" && entry.Message.Contains("nothing was reclaimed"));
        Assert.Equal("completed", (await StateAsync(app, SystemTasks.SharingReclaim)).LastResult);
    }

    // ------------------------------------------------------------------ the download-polling service

    private static async Task<(ApplicationTestHost App, FakeTimeProvider Clock)> StartOnFakeClockAsync(Action<IServiceCollection>? more = null)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            more?.Invoke(services);
        });
        return (app, clock);
    }

    /// <summary>Moves the clock in small steps (the service runs on its own thread) until the condition holds or the limit is reached.</summary>
    private static async Task<bool> AdvanceUntilAsync(FakeTimeProvider clock, Func<Task<bool>> done, TimeSpan step, TimeSpan limit)
    {
        for (var moved = TimeSpan.Zero; moved < limit; moved += step)
        {
            if (await done()) return true;
            clock.Advance(step);
            await Task.Delay(25); // reason: lets the service's own thread react to each fake-clock step; it has no signal to await, and the loop is bounded by limit
        }

        return await done();
    }

    private sealed class ScriptedPolling(Func<int, Task<DownloadDispatchPollingReport>> poll) : IDownloadDispatchPollingService
    {
        public int Calls { get; private set; }

        public Task<DownloadDispatchPollingReport> PollAsync(CancellationToken cancellationToken) => poll(++Calls);
    }

    private static readonly DownloadDispatchPollingReport NothingFound = new(0, 0, 0, 0, 0, 0);

    [Fact]
    public async Task A_polling_pass_that_fails_is_recorded_failed_and_logged_and_runs_again_when_it_is_next_due()
    {
        var (app, clock) = await StartOnFakeClockAsync();
        await using var _ = app;
        var jobs = app.Services.GetRequiredService<IJobQueueRepository>();
        var polling = new ScriptedPolling(call => call == 1 ? throw new InvalidOperationException("download state could not be read") : Task.FromResult(NothingFound));
        var logger = new CapturingLogger<DownloadDispatchPollingHostedService>();
        var service = new DownloadDispatchPollingHostedService(logger, polling, jobs, clock);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await AdvanceUntilAsync(clock, async () => await ResultAsync(app, SystemTasks.DownloadDispatchPolling) == "failed", TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(2)), "the first pass should run and fail");
            Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("Error occurred during download dispatch polling"));
            var failed = await StateAsync(app, SystemTasks.DownloadDispatchPolling);
            Assert.True(failed.NextRunUtc > clock.GetUtcNow(), "a failed pass is not tried again at once");

            Assert.True(await AdvanceUntilAsync(clock, async () => await ResultAsync(app, SystemTasks.DownloadDispatchPolling) == "completed", TimeSpan.FromMinutes(5), TimeSpan.FromHours(3)), "the pass should run again once it is due and complete");
            Assert.Equal(2, polling.Calls);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_polling_pass_stopped_by_shutdown_is_recorded_cancelled()
    {
        var (app, clock) = await StartOnFakeClockAsync();
        await using var _ = app;
        var cancellable = new CancellablePolling();
        var service = new DownloadDispatchPollingHostedService(NullLogger<DownloadDispatchPollingHostedService>.Instance, cancellable, app.Services.GetRequiredService<IJobQueueRepository>(), clock);

        await service.StartAsync(CancellationToken.None);
        Assert.True(await AdvanceUntilAsync(clock, () => Task.FromResult(cancellable.Started.Task.IsCompleted), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(2)), "the pass should have started");
        await service.StopAsync(CancellationToken.None);

        Assert.Equal("cancelled", (await StateAsync(app, SystemTasks.DownloadDispatchPolling)).LastResult);
    }

    private sealed class CancellablePolling : IDownloadDispatchPollingService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DownloadDispatchPollingReport> PollAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new DownloadDispatchPollingReport(0, 0, 0, 0, 0, 0);
        }
    }

    /// <summary>Forwards everything to the real job store, except the claim, which fails the first time it is asked.</summary>
    public class ClaimBreaksOnce : DispatchProxy
    {
        public IJobQueueRepository Real { get; set; } = null!;

        public int Failures { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IJobQueueRepository.TryClaimScheduledPassAsync) && Failures++ == 0)
            {
                return Task.FromException<bool>(new InvalidOperationException("the job store was busy"));
            }

            try
            {
                return targetMethod.Invoke(Real, args);
            }
            catch (TargetInvocationException exception)
            {
                throw exception.InnerException ?? exception;
            }
        }
    }

    [Fact]
    public async Task A_polling_pass_whose_claim_cannot_be_made_is_logged_and_the_service_goes_on_to_the_next_check()
    {
        var (app, clock) = await StartOnFakeClockAsync();
        await using var _ = app;
        var proxy = DispatchProxy.Create<IJobQueueRepository, ClaimBreaksOnce>();
        ((ClaimBreaksOnce)(object)proxy).Real = app.Services.GetRequiredService<IJobQueueRepository>();
        var polling = new ScriptedPolling(_ => Task.FromResult(NothingFound));
        var logger = new CapturingLogger<DownloadDispatchPollingHostedService>();
        var service = new DownloadDispatchPollingHostedService(logger, polling, proxy, clock);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await AdvanceUntilAsync(clock, () => Task.FromResult(polling.Calls == 1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30)), "the next check should claim and run the pass");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("Error occurred while scheduling download dispatch polling"));
        Assert.Equal("completed", (await StateAsync(app, SystemTasks.DownloadDispatchPolling)).LastResult);
    }

    [Fact]
    public async Task Three_failed_polling_passes_in_a_row_pause_the_poller_and_Health_says_so_until_it_resumes()
    {
        var (app, clock) = await StartOnFakeClockAsync();
        await using var _ = app;
        var inner = new ScriptedPolling(_ => throw new InvalidOperationException("download state could not be read"));
        var breaker = new CircuitBreakerDownloadDispatchPollingService(inner, NullLogger<CircuitBreakerDownloadDispatchPollingService>.Instance);
        var service = new DownloadDispatchPollingHostedService(NullLogger<DownloadDispatchPollingHostedService>.Instance, breaker, app.Services.GetRequiredService<IJobQueueRepository>(), clock);
        var check = new DispatchRecoveryPollerHealthCheck(breaker);
        Assert.Equal(HealthCheckStatuses.Ok, (await check.RunAsync(CancellationToken.None)).Single().Status);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await AdvanceUntilAsync(clock, () => Task.FromResult(breaker.ConsecutiveFailures >= 3), TimeSpan.FromMinutes(5), TimeSpan.FromHours(6)), "three passes should fail, one hour apart");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.True(breaker.IsOpen);
        var outcome = (await check.RunAsync(CancellationToken.None)).Single();
        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Contains("paused checking downloads", outcome.Message);
        Assert.Contains("3 failed passes in a row", outcome.Message);
    }

    [Fact]
    public async Task The_polling_pass_runs_although_automatic_jobs_are_switched_off()
    {
        var (app, clock) = await StartOnFakeClockAsync();
        await using var _ = app;
        Assert.False((await app.Client.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("autoStartJobs").GetBoolean(), "a fresh install has automatic jobs off");
        var polling = new ScriptedPolling(_ => Task.FromResult(NothingFound));
        var service = new DownloadDispatchPollingHostedService(NullLogger<DownloadDispatchPollingHostedService>.Instance, polling, app.Services.GetRequiredService<IJobQueueRepository>(), clock);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await AdvanceUntilAsync(clock, () => Task.FromResult(polling.Calls == 1), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(2)), "the pass is not gated by the automatic jobs switch");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal("completed", (await StateAsync(app, SystemTasks.DownloadDispatchPolling)).LastResult);
    }

    // ------------------------------------------------------------------ helpers

    internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_entries) { return [.. _entries]; } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries) { _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception)); }
        }
    }
}
