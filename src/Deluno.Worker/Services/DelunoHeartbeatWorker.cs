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
        // "library.subtitles.scan" is here for the same reason: it is a
        // directory listing and an ffprobe per file, which is the import lane's
        // resource exactly. It is deliberately not on a search lane — reading
        // what a file already contains has nothing to do with an indexer, and
        // putting it there would make a subtitle scan able to delay a search.
        new("import", TimeSpan.FromSeconds(30), ["filesystem.import.execute", "library.import.existing", "library.subtitles.scan"],
            BatchSize: 16, MaxConcurrency: 8),

        // Searching, one lane per catalogue so neither can starve the other.
        //
        // These were one lane at MaxConcurrency 2, to stop searches "multiplying
        // outbound requests against the same remote hosts". That concern is
        // real and it is **already handled one layer down**:
        // `FeedMediaSearchPlanner` paces every request through
        // `outboundRequestThrottle`, keyed on the *host* rather than the indexer
        // id, precisely because two indexer entries can point at one tracker.
        //
        // So the shared narrow lane protected no tracker — the throttle does
        // that. All it did was make a TV search wait behind movie searches, and
        // it was at its worst in exactly the case that matters: movie searches
        // stuck against an unresponsive indexer, holding the lane while TV work
        // sat queued behind them.
        //
        // Measured before splitting (`JobQueueContentionBenchmark`): 25
        // concurrent workers sustain ~4,300 lease+complete round trips a second
        // against the shared jobs database, every lane draining evenly — so the
        // queue does not care how many lanes there are.
        //
        // `episode.search` rides with TV because it is the same catalogue and
        // the same work at a finer grain.
        new("search.movies", TimeSpan.FromSeconds(30), [LibrarySearchJobTypes.Movies],
            BatchSize: 4, MaxConcurrency: 4),
        new("search.tv", TimeSpan.FromSeconds(30), [LibrarySearchJobTypes.Tv, "episode.search"],
            BatchSize: 4, MaxConcurrency: 4),

        // Remote list providers, and rate-limited by them.
        //
        // "library.subtitles.search" rides here, and the choice is deliberate.
        // DESIGN-002 rule 3 says Subber gets no lane of its own, so the question
        // is which existing one, and the answer is by *resource*: this is
        // outbound HTTP to a third party that rate limits us, which is exactly
        // what this lane already is. The search lanes were wrong for it for the
        // same reason the scan is not on them — a subtitle fetch has nothing to
        // do with an indexer, and putting it there would let a slow provider
        // delay a release search.
        new("intake", TimeSpan.FromSeconds(30), ["intake.sync", "library.subtitles.search"],
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

        // What this lane is running right now. Kept across ticks, so a tick can
        // top the lane up rather than waiting for the previous batch to drain.
        var inFlight = new List<Task>();

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
                logger.LogDebug("Worker {WorkerId} lane {LaneName} tick with no pending jobs.", _workerId, lane.Name);
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
