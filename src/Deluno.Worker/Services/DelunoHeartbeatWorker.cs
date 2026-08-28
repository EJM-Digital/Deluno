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
    /// <summary>
    /// How long a lane may reuse the last settings snapshot.
    ///
    /// <para>One second was chosen when there were seven lanes on one tick and
    /// they mostly missed it anyway. Every lane reads this on every wake purely
    /// to find out whether background work is switched on — a question whose
    /// answer changes about once a month — so a wider window costs nothing and
    /// removes most of the reads outright. Fifteen seconds is still faster than
    /// anybody can notice a pause not taking effect.</para>
    /// </summary>
    private static readonly TimeSpan SettingsCacheWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private readonly object _settingsSync = new();
    private readonly object _heartbeatSync = new();
    private PlatformSettingsSnapshot? _cachedSettings;
    private DateTimeOffset _cachedSettingsUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHeartbeatUtc = DateTimeOffset.MinValue;
    private readonly JobLane[] _lanes = [.. JobLanes.All];

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

        // How long to wait before looking again, when nothing wakes this lane
        // first. Set from the lane's own next scheduled job at the end of each
        // idle tick, so a lane sleeps exactly as long as it should rather than
        // asking every thirty seconds whether anything has changed.
        var sleepFor = lane.Interval;

        // What this lane is running right now. Kept across ticks, so a tick can
        // top the lane up rather than waiting for the previous batch to drain.
        var inFlight = new List<Task>();

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!drainImmediately)
            {
                // Signal first, then this lane's own next due time, and the
                // backstop only if there is no such time. Nothing here is shared
                // with another kind of work — which is the point: a lane wakes
                // when its own work is ready and not when somebody else's tick
                // came round.
                await gate.WaitAsync(sleepFor, stoppingToken);
            }

            drainImmediately = false;
            sleepFor = lane.Interval;

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
                        SearchWindowEndHour: library.SearchWindowEndHour,
                        WantsSubtitles: library.SubtitleLanguages is { Count: > 0 }))
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

            // Lease only what there is room to start.
            //
            // This used to lease a whole batch and then `await` all of it before
            // going round again, which made the batch a **barrier**: a lane with
            // one slow job sat with its other slots empty and leased nothing new
            // until that job finished. A single search stuck behind an
            // unresponsive indexer held up every search behind it, and a large
            // import held up fifteen small ones — not because anything was
            // contended, but because they were in the same batch.
            //
            // Now each lane keeps up to MaxConcurrency jobs in flight and tops
            // up as each one finishes. Nothing waits on anything except a real
            // shortage of slots.
            inFlight.RemoveAll(task => task.IsCompleted);
            var freeSlots = lane.MaxConcurrency - inFlight.Count;
            if (freeSlots <= 0)
            {
                // Every slot busy. Come back when one frees rather than leasing
                // work this lane has nowhere to run.
                continue;
            }

            var jobs = await jobQueueRepository.LeaseBatchAsync(
                $"{_workerId}-{lane.Name}",
                TimeSpan.FromMinutes(2),
                lane.JobTypes,
                Math.Min(lane.BatchSize, freeSlots),
                stoppingToken);

            if (jobs.Count == 0)
            {
                // Nothing to run. Rather than come back on a tick and ask again,
                // find out when there could be something and sleep until then —
                // a signal still wakes it sooner. On an idle install this is one
                // indexed lookup and then silence, where the fixed tick was a
                // lease query per lane per thirty seconds for ever.
                var nextDue = await jobQueueRepository.NextDueUtcAsync(lane.JobTypes, stoppingToken);
                if (nextDue is not null)
                {
                    var until = nextDue.Value - timeProvider.GetUtcNow();
                    // Never longer than the backstop, and never a negative or
                    // zero wait — a due time in the past means something is
                    // holding it back, and spinning on that would be worse than
                    // the polling this replaced.
                    sleepFor = until <= TimeSpan.Zero
                        ? TimeSpan.FromSeconds(1)
                        : until < lane.Interval ? until : lane.Interval;
                }

                logger.LogDebug(
                    "Worker {WorkerId} lane {LaneName} has nothing to run; sleeping {SleepSeconds}s.",
                    _workerId,
                    lane.Name,
                    (int)sleepFor.TotalSeconds);
                continue;
            }

            // Took everything offered, so there may well be more queued. Go
            // straight round rather than pacing a backlog by the interval.
            if (jobs.Count == Math.Min(lane.BatchSize, freeSlots))
            {
                drainImmediately = true;
            }

            foreach (var job in jobs)
            {
                inFlight.Add(RunJobAsync(lane, job, stoppingToken));
            }
        }

        // Shutdown: let what is running finish rather than abandoning leases
        // that would then have to be recovered on the next start.
        if (inFlight.Count > 0)
        {
            await Task.WhenAll(inFlight);
        }
    }

    /// <summary>
    /// One job, start to finish, in its own DI scope.
    ///
    /// Its own scope because a job now outlives the tick that leased it — and
    /// because it is safer besides: the previous shape shared one scope across
    /// every job running concurrently in a batch, which quietly required every
    /// handler to be stateless. A scope per job removes that requirement rather
    /// than documenting it.
    ///
    /// Failure stays per job. One job's exception is recorded against that job
    /// and touches nothing else in the lane.
    /// </summary>
    private async Task RunJobAsync(JobLane lane, JobQueueItem job, CancellationToken stoppingToken)
    {
        var workerId = $"{_workerId}-{lane.Name}";

        using var scope = scopeFactory.CreateScope();
        var jobQueueRepository = scope.ServiceProvider.GetRequiredService<IJobQueueRepository>();

        try
        {
            logger.LogInformation("Processing job {JobId} of type {JobType} on lane {LaneName}.", job.Id, job.JobType, lane.Name);
            var handler = scope.ServiceProvider.GetRequiredService<JobHandlerRegistry>().Resolve(job.JobType);
            var message = await handler.HandleAsync(job, stoppingToken);

            await jobQueueRepository.CompleteAsync(job.Id, workerId, message, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down. Leave the lease to expire and be recovered on the
            // next start rather than recording a failure the job did not have.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Worker {WorkerId} lane {LaneName} failed processing job {JobId}.", _workerId, lane.Name, job.Id);
            await jobQueueRepository.FailAsync(job.Id, workerId, ex.Message, CancellationToken.None);
        }
    }

}
