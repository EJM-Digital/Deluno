using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Recovery.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Intake;
using Deluno.Worker.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Deluno.Connections.Data;
using Deluno.Recovery.Services;

namespace Deluno.Worker.Services;

public sealed class DelunoHeartbeatWorker(
    ILogger<DelunoHeartbeatWorker> logger,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IJobLaneSignal laneSignal)
    : BackgroundService
{
    private readonly string _workerId = $"worker-{Environment.MachineName.ToLowerInvariant()}";
    private static readonly TimeSpan SettingsCacheWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private readonly object _settingsSync = new();
    private readonly object _heartbeatSync = new();
    private PlatformSettingsSnapshot? _cachedSettings;
    private DateTimeOffset _cachedSettingsUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHeartbeatUtc = DateTimeOffset.MinValue;
    private readonly JobLane[] _lanes =
    [
        // Planning only. Deciding what should run is cheap and must not sit
        // behind a long-running job, so it gets its own lane and executes
        // nothing itself. It has no job types of its own to be woken by, so it
        // is signalled through the "planning.wake" sentinel instead — notably
        // by RequestLibrarySearchAsync, the path the Search button takes.
        new("planning", TimeSpan.FromSeconds(30), [],
            PlanAutomation: true, PlanImports: true, PlanMaintenance: true,
            SignalTypesOverride: ["planning.wake"]),

        // Disk-bound. The widest lane: imports are the backlog users actually
        // feel, and the work is mostly waiting on file I/O.
        //
        // "library.import.existing" belongs here too: it is the same resource.
        // Each of its jobs is one bounded slice of a library scan, so it queues
        // and drains like any other import rather than holding a lease for
        // hours.
        new("import", TimeSpan.FromSeconds(30), ["filesystem.import.execute", "library.import.existing"],
            BatchSize: 16, MaxConcurrency: 8),

        // Indexer-bound. Deliberately narrow — each job already fans out across
        // every configured indexer internally, so stacking many searches at once
        // multiplies outbound requests against the same remote hosts.
        //
        // "episode.search" belongs here, not in its own lane: it contends on the
        // exact same indexers as "library.search" and has to share the budget.
        new("search", TimeSpan.FromSeconds(30), ["library.search", "episode.search"],
            BatchSize: 4, MaxConcurrency: 2),

        // Remote list providers, and rate-limited by them.
        new("intake", TimeSpan.FromSeconds(30), ["intake.sync"],
            BatchSize: 4, MaxConcurrency: 2),

        // Metadata provider HTTP. Separate from catalogue work so a slow
        // provider cannot stall local recalculation.
        new("metadata", TimeSpan.FromSeconds(30), ["movies.metadata.refresh", "series.metadata.refresh"],
            BatchSize: 8, MaxConcurrency: 4),

        // Local only: SQLite and CPU, no network. Safe to run wide.
        new("catalog", TimeSpan.FromSeconds(30),
        [
            "movies.quality.recalculate",
            "series.quality.recalculate",
            "movies.catalog.refresh",
            "series.catalog.refresh"
        ], BatchSize: 16, MaxConcurrency: 8)
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Deluno worker runtime started as {WorkerId}.", _workerId);

        using (var scope = scopeFactory.CreateScope())
        {
            AssertHandlerRoutingIsComplete(scope.ServiceProvider.GetRequiredService<JobHandlerRegistry>());
            var recoveredJobs = await scope.ServiceProvider.GetRequiredService<IJobQueueRepository>().ReleaseLeasesAsync(
                _lanes.Select(lane => $"{_workerId}-{lane.Name}").ToArray(),
                stoppingToken);
            if (recoveredJobs.Count > 0)
            {
                logger.LogInformation(
                    "Worker {WorkerId} released {JobCount} lease(s) held by its previous incarnation.",
                    _workerId,
                    recoveredJobs.Count);
            }
        }

        await Task.WhenAll(_lanes.Select(lane => RunLaneAsync(lane, stoppingToken)));
    }

    /// <summary>
    /// Fails fast at startup if a registered handler's job type is not routed
    /// by exactly one lane. This is what closes the gap that let
    /// "episode.search" have a handler but no lane willing to lease it — the
    /// job would have sat queued forever with nothing visibly wrong.
    /// </summary>
    private void AssertHandlerRoutingIsComplete(JobHandlerRegistry registry)
    {
        var laneJobTypeCounts = _lanes
            .SelectMany(lane => lane.JobTypes)
            .GroupBy(jobType => jobType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var jobType in registry.RegisteredJobTypes)
        {
            if (!laneJobTypeCounts.TryGetValue(jobType, out var count) || count == 0)
            {
                throw new InvalidOperationException(
                    $"Job handler for type '{jobType}' is registered but no lane will ever lease it.");
            }

            if (count > 1)
            {
                throw new InvalidOperationException(
                    $"Job type '{jobType}' appears in {count} lanes. It must be routed by exactly one lane.");
            }
        }
    }

    /// <summary>
    /// The settings snapshot, shared by all lanes and refreshed at most once a
    /// second.
    ///
    /// Every lane used to read this from SQLite on every tick purely to check
    /// AutoStartJobs — roughly 50 reads a minute between them, of a row that
    /// changes when a human edits a setting. A one-second window keeps the gate
    /// responsive while removing effectively all of that traffic.
    /// </summary>
    private async Task<PlatformSettingsSnapshot> ReadSettingsAsync(
        IPlatformSettingsRepository repository,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        lock (_settingsSync)
        {
            if (_cachedSettings is not null && now - _cachedSettingsUtc < SettingsCacheWindow)
            {
                return _cachedSettings;
            }
        }

        var settings = await repository.GetAsync(cancellationToken);

        lock (_settingsSync)
        {
            _cachedSettings = settings;
            _cachedSettingsUtc = now;
        }

        return settings;
    }

    /// <summary>
    /// Liveness, not a per-tick obligation. Three lanes writing this row every
    /// 2, 5 and 8 seconds produced ~39 writes a minute to say the same thing;
    /// the lease recovery window is measured in minutes, so once every 15
    /// seconds is ample.
    ///
    /// Deliberately still per-process, unlike the recurring passes in
    /// <see cref="WorkPlanner"/>: each host must report its own liveness, so a
    /// clock shared across hosts would be wrong here.
    /// </summary>
    private async Task HeartbeatIfDueAsync(
        IJobQueueRepository jobQueueRepository,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        lock (_heartbeatSync)
        {
            if (now - _lastHeartbeatUtc < HeartbeatInterval)
            {
                return;
            }

            _lastHeartbeatUtc = now;
        }

        await jobQueueRepository.HeartbeatAsync(_workerId, cancellationToken);
    }

    private async Task RunLaneAsync(JobLane lane, CancellationToken stoppingToken)
    {
        if (!lane.Enabled)
        {
            logger.LogInformation("Worker {WorkerId} lane {LaneName} disabled; not starting.", _workerId, lane.Name);
            return;
        }

        // Stagger lane starts so lanes on 1/2/3/5-second timers do not all land
        // on the same second and queue behind one SQLite writer. Applied once at
        // lane start, not per tick, so the cadence stays predictable afterwards.
        if (lane.Jitter > TimeSpan.Zero)
        {
            var jitterMilliseconds = Random.Shared.Next(0, (int)lane.Jitter.TotalMilliseconds);
            await Task.Delay(jitterMilliseconds, stoppingToken);
        }

        var gate = laneSignal.Register(lane.Name, lane.SignalTypes);
        logger.LogInformation(
            "Worker {WorkerId} lane {LaneName} started for {JobTypes}.",
            _workerId,
            lane.Name,
            string.Join(", ", lane.JobTypes));

        // Drained a full batch last time round — more work is likely still
        // queued, so skip the wait and go straight back to leasing instead of
        // pacing a backlog by the interval.
        var drainImmediately = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!drainImmediately)
            {
                // Signal first, interval as a backstop. The backstop still has
                // to exist: it covers jobs made ready by scheduled_utc passing,
                // lease recovery after a crash, and anything enqueued by
                // another process that does not go through this signal.
                await gate.WaitAsync(lane.Interval, stoppingToken);
            }

            drainImmediately = false;

            using var scope = scopeFactory.CreateScope();
            var services = scope.ServiceProvider;

            // Resolve only what the gate needs. The rest of the graph is
            // resolved further down, once there is actually work to do — most
            // ticks on an idle install get no further than the next few lines.
            var jobQueueRepository = services.GetRequiredService<IJobQueueRepository>();
            var platformSettingsRepository = services.GetRequiredService<IPlatformSettingsRepository>();
            var processorRepository = services.GetRequiredService<IProcessorRepository>();
            var librariesRepository = services.GetRequiredService<ILibrariesRepository>();

            // Liveness is independent of whether background automation is
            // enabled. A paused installation is still running and must not
            // look unavailable to the readiness endpoint.
            await HeartbeatIfDueAsync(jobQueueRepository, stoppingToken);

            var settings = await ReadSettingsAsync(platformSettingsRepository, stoppingToken);
            if (!settings.AutoStartJobs)
            {
                logger.LogDebug("Worker {WorkerId} lane {LaneName} tick with auto-start disabled.", _workerId, lane.Name);
                continue;
            }

            var jobScheduler = services.GetRequiredService<IJobScheduler>();
            var workPlanner = services.GetRequiredService<WorkPlanner>();
            var downloadClientTelemetryService = services.GetRequiredService<IDownloadClientTelemetryService>();
            var processorConnectionService = services.GetRequiredService<IProcessorConnectionService>();
            var movieCatalogRepository = services.GetRequiredService<IMovieCatalogRepository>();
            var seriesCatalogRepository = services.GetRequiredService<ISeriesCatalogRepository>();
            var activityFeedRepository = services.GetRequiredService<IActivityFeedRepository>();
            var intakeSyncService = services.GetRequiredService<IIntakeSyncService>();

            if (lane.PlanAutomation)
            {
                var libraries = await librariesRepository.ListLibrariesAsync(stoppingToken);
                var automationPlans = libraries
                    .Select(library => new LibraryAutomationPlanItem(
                        LibraryId: library.Id,
                        LibraryName: library.Name,
                        MediaType: library.MediaType,
                        AutoSearchEnabled: library.AutoSearchEnabled,
                        MissingSearchEnabled: library.MissingSearchEnabled,
                        UpgradeSearchEnabled: library.UpgradeSearchEnabled,
                        SearchIntervalHours: library.SearchIntervalHours,
                        RetryDelayHours: library.RetryDelayHours,
                        MaxItemsPerRun: library.MaxItemsPerRun,
                        SearchWindowStartHour: library.SearchWindowStartHour,
                        SearchWindowEndHour: library.SearchWindowEndHour))
                    .ToArray();

                await jobQueueRepository.PlanLibrarySearchesAsync(automationPlans, stoppingToken);
                await workPlanner.PlanIntakeAutomationAsync(intakeSyncService, stoppingToken);
            }

            if (lane.PlanImports)
            {
                await workPlanner.PlanLibraryImportResumeAsync(
                    jobScheduler,
                    services.GetRequiredService<IExistingLibraryImportService>(),
                    timeProvider,
                    stoppingToken);

                await workPlanner.PlanImportAutomationAsync(
                    jobScheduler,
                    processorRepository,
                    librariesRepository,
                    downloadClientTelemetryService,
                    processorConnectionService,
                    activityFeedRepository,
                    movieCatalogRepository,
                    seriesCatalogRepository,
                    timeProvider,
                    stoppingToken);

                // After importing, let go of anything that has finished sharing.
                await workPlanner.PlanSharingReclaimAsync(
                    downloadClientTelemetryService,
                    scope.ServiceProvider.GetRequiredService<IPlatformSettingsRepository>(),
                    scope.ServiceProvider.GetRequiredService<IConnectionsRepository>(),
                    librariesRepository,
                    activityFeedRepository,
                    scope.ServiceProvider.GetRequiredService<IDownloadSharingRepository>(),
                    movieCatalogRepository,
                    seriesCatalogRepository,
                    scope.ServiceProvider.GetRequiredService<SharingReclaimService>(),
                    stoppingToken);
            }

            if (lane.PlanMaintenance)
            {
                var cleanupService = scope.ServiceProvider.GetRequiredService<IDispatchCleanupService>();
                var downloadRetryService = scope.ServiceProvider.GetRequiredService<IDownloadRetryService>();
                await workPlanner.RunDispatchCleanupAsync(cleanupService, stoppingToken);
                await workPlanner.RunDispatchRetryPassAsync(downloadRetryService, stoppingToken);
                await workPlanner.PlanMetadataRefreshAutomationAsync(
                    jobScheduler,
                    movieCatalogRepository,
                    seriesCatalogRepository,
                    timeProvider,
                    stoppingToken);
            }

            // A planning-only lane has nothing to execute.
            if (lane.JobTypes.Count == 0)
            {
                continue;
            }

            // Lease a batch, not a single job. One job per tick made sustained
            // throughput a function of the timer — the 2-second import lane
            // could never exceed 30 jobs a minute however much was queued.
            var jobs = await jobQueueRepository.LeaseBatchAsync(
                $"{_workerId}-{lane.Name}",
                TimeSpan.FromMinutes(2),
                lane.JobTypes,
                lane.BatchSize,
                stoppingToken);

            if (jobs.Count == 0)
            {
                logger.LogDebug("Worker {WorkerId} lane {LaneName} tick with no pending jobs.", _workerId, lane.Name);
                continue;
            }

            // A full batch means the backlog may not be drained yet. Loop
            // straight round instead of waiting out the interval — otherwise a
            // 500-job backlog is still paced by the backstop.
            if (jobs.Count == lane.BatchSize)
            {
                drainImmediately = true;
            }

            // Only reached when there is a job, so it never costs anything on an
            // idle tick.
            var handlerRegistry = services.GetRequiredService<JobHandlerRegistry>();

            // Jobs in a batch are independent, so they run together rather than
            // queueing behind each other. Failure stays per job: one job's
            // exception is recorded against that job and does not touch the rest.
            //
            // Handlers are resolved once per job from the batch's shared DI
            // scope. They must stay stateless — a scope is shared across every
            // job running concurrently in this batch.
            await Parallel.ForEachAsync(
                jobs,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = lane.MaxConcurrency,
                    CancellationToken = stoppingToken
                },
                async (job, token) =>
                {
                    try
                    {
                        logger.LogInformation("Processing job {JobId} of type {JobType} on lane {LaneName}.", job.Id, job.JobType, lane.Name);
                        var handler = handlerRegistry.Resolve(job.JobType);
                        var message = await handler.HandleAsync(job, token);

                        await jobQueueRepository.CompleteAsync(job.Id, $"{_workerId}-{lane.Name}", message, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Worker {WorkerId} lane {LaneName} failed processing job {JobId}.", _workerId, lane.Name, job.Id);
                        await jobQueueRepository.FailAsync(job.Id, $"{_workerId}-{lane.Name}", ex.Message, CancellationToken.None);
                    }
                });
        }
    }

    /// <summary>
    /// One lane. Lanes are separated by the resource they contend on, not by a
    /// generic "maintenance" grouping — metadata refreshes wait on a remote
    /// metadata provider, imports wait on disk, searches wait on indexers, and
    /// a catalogue recalculation waits on nothing but SQLite. Putting those in
    /// one lane made each of them wait behind the others for no reason.
    /// </summary>
    /// <param name="Interval">
    /// A backstop, not the primary trigger — the lane is normally woken by
    /// <see cref="IJobLaneSignal"/> as soon as matching work is enqueued. This
    /// still covers jobs made ready by their scheduled time passing, lease
    /// recovery after a crash, and work enqueued through a path that does not
    /// signal.
    /// </param>
    /// <param name="JobTypes">
    /// Empty means the lane only plans work and never executes it.
    /// </param>
    /// <param name="BatchSize">Jobs claimed per tick.</param>
    /// <param name="MaxConcurrency">Jobs from that batch run at once.</param>
    /// <param name="Enabled">Whether this lane starts at all.</param>
    /// <param name="Jitter">
    /// A random delay up to this length applied once before the lane's first
    /// tick, so lanes on the same or nearby intervals do not all wake and hit
    /// SQLite in the same instant. Defaults to 25% of <see cref="Interval"/>.
    /// </param>
    /// <param name="SignalTypesOverride">
    /// The job types this lane registers with <see cref="IJobLaneSignal"/> to be
    /// woken by. Defaults to <see cref="JobTypes"/>; a planning-only lane (empty
    /// <see cref="JobTypes"/>) needs an explicit override, since it executes no
    /// job type but still wants to be signalled.
    /// </param>
    private sealed record JobLane(
        string Name,
        TimeSpan Interval,
        IReadOnlyList<string> JobTypes,
        bool PlanAutomation = false,
        bool PlanImports = false,
        bool PlanMaintenance = false,
        int BatchSize = 8,
        int MaxConcurrency = 4,
        bool Enabled = true,
        TimeSpan? JitterOverride = null,
        IReadOnlyList<string>? SignalTypesOverride = null)
    {
        public TimeSpan Jitter { get; init; } = JitterOverride ?? TimeSpan.FromMilliseconds(Interval.TotalMilliseconds * 0.25);

        public IReadOnlyList<string> SignalTypes { get; init; } = SignalTypesOverride ?? JobTypes;
    }
}
