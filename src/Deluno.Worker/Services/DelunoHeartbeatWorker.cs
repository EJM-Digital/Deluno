using Deluno.Jobs.Data;
using Deluno.Filesystem;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Metadata;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Movies.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Series.Contracts;
using Deluno.Worker.Intake;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;

namespace Deluno.Worker.Services;

public sealed class DelunoHeartbeatWorker(
    ILogger<DelunoHeartbeatWorker> logger,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider)
    : BackgroundService
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _workerId = $"worker-{Environment.MachineName.ToLowerInvariant()}";
    private static readonly TimeSpan SettingsCacheWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private readonly object _settingsSync = new();
    private readonly object _heartbeatSync = new();
    private PlatformSettingsSnapshot? _cachedSettings;
    private DateTimeOffset _cachedSettingsUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHeartbeatUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastImportAutomationUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDispatchCleanupUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDispatchRetryPassUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastMetadataAutomationUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastIntakeAutomationUtc = DateTimeOffset.MinValue;
    private readonly JobLane[] _lanes =
    [
        // Planning only. Deciding what should run is cheap and must not sit
        // behind a long-running job, so it gets its own lane and executes
        // nothing itself.
        new("planning", TimeSpan.FromSeconds(5), [],
            PlanAutomation: true, PlanImports: true, PlanMaintenance: true),

        // Disk-bound. The widest lane: imports are the backlog users actually
        // feel, and the work is mostly waiting on file I/O.
        new("import", TimeSpan.FromSeconds(1), ["filesystem.import.execute"],
            BatchSize: 16, MaxConcurrency: 8),

        // Indexer-bound. Deliberately narrow — each job already fans out across
        // every configured indexer internally, so stacking many searches at once
        // multiplies outbound requests against the same remote hosts.
        new("search", TimeSpan.FromSeconds(2), ["library.search"],
            BatchSize: 4, MaxConcurrency: 2),

        // Remote list providers, and rate-limited by them.
        new("intake", TimeSpan.FromSeconds(5), ["intake.sync"],
            BatchSize: 4, MaxConcurrency: 2),

        // Metadata provider HTTP. Separate from catalogue work so a slow
        // provider cannot stall local recalculation.
        new("metadata", TimeSpan.FromSeconds(3), ["movies.metadata.refresh", "series.metadata.refresh"],
            BatchSize: 8, MaxConcurrency: 4),

        // Local only: SQLite and CPU, no network. Safe to run wide.
        new("catalog", TimeSpan.FromSeconds(3),
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

        await Task.WhenAll(_lanes.Select(lane => RunLaneAsync(lane, stoppingToken)));
    }

    /// <summary>
    /// The settings snapshot, shared by all three lanes and refreshed at most
    /// once a second.
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
        using var timer = new PeriodicTimer(lane.Interval);
        logger.LogInformation(
            "Worker {WorkerId} lane {LaneName} started for {JobTypes}.",
            _workerId,
            lane.Name,
            string.Join(", ", lane.JobTypes));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            using var scope = scopeFactory.CreateScope();
            var services = scope.ServiceProvider;

            // Resolve only what the gate needs. The rest of the graph is
            // resolved further down, once there is actually work to do — most
            // ticks on an idle install get no further than the next few lines.
            var jobQueueRepository = services.GetRequiredService<IJobQueueRepository>();
            var platformSettingsRepository = services.GetRequiredService<IPlatformSettingsRepository>();
            var librariesRepository = services.GetRequiredService<ILibrariesRepository>();
            var qualityRepository = services.GetRequiredService<IQualityRepository>();

            // The gate goes first. It used to be the fourth thing that
            // happened, after a heartbeat write and a settings read, so an
            // install with automation switched off still paid for two database
            // round trips per lane tick, forever.
            var settings = await ReadSettingsAsync(platformSettingsRepository, stoppingToken);
            if (!settings.AutoStartJobs)
            {
                logger.LogDebug("Worker {WorkerId} lane {LaneName} tick with auto-start disabled.", _workerId, lane.Name);
                continue;
            }

            await HeartbeatIfDueAsync(jobQueueRepository, stoppingToken);

            var jobScheduler = services.GetRequiredService<IJobScheduler>();
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
                    .Select(library => new Deluno.Jobs.Contracts.LibraryAutomationPlanItem(
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
                await PlanIntakeAutomationAsync(intakeSyncService, timeProvider, stoppingToken);
            }

            if (lane.PlanImports)
            {
                await PlanImportAutomationAsync(
                    jobScheduler,
                    jobQueueRepository,
                    platformSettingsRepository,
                    librariesRepository,
                    qualityRepository,
                    downloadClientTelemetryService,
                    processorConnectionService,
                    activityFeedRepository,
                    movieCatalogRepository,
                    seriesCatalogRepository,
                    timeProvider,
                    stoppingToken);
            }

            if (lane.PlanMaintenance)
            {
                var cleanupService = scope.ServiceProvider.GetRequiredService<IDispatchCleanupService>();
                var downloadRetryService = scope.ServiceProvider.GetRequiredService<IDownloadRetryService>();
                await RunDispatchCleanupAsync(cleanupService, timeProvider, stoppingToken);
                await RunDispatchRetryPassAsync(downloadRetryService, timeProvider, stoppingToken);
                var jobList = await jobQueueRepository.ListAsync(600, stoppingToken);
                await PlanMetadataRefreshAutomationAsync(
                    jobScheduler,
                    movieCatalogRepository,
                    seriesCatalogRepository,
                    jobList,
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

            // Only reached when there is a job, so these never cost anything on
            // an idle tick.
            var acquisitionPipeline = services.GetRequiredService<IAcquisitionDecisionPipeline>();
            var downloadClientGrabService = services.GetRequiredService<IDownloadClientGrabService>();
            var metadataProvider = services.GetRequiredService<IMetadataProvider>();
            var importPipelineService = services.GetRequiredService<IImportPipelineService>();

            // Jobs in a batch are independent, so they run together rather than
            // queueing behind each other. Failure stays per job: one job's
            // exception is recorded against that job and does not touch the rest.
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
                        var message = await ProcessJobAsync(
                            job,
                            jobQueueRepository,
                            platformSettingsRepository,
                            librariesRepository,
                            qualityRepository,
                            acquisitionPipeline,
                            downloadClientGrabService,
                            metadataProvider,
                            importPipelineService,
                            movieCatalogRepository,
                            seriesCatalogRepository,
                            activityFeedRepository,
                            intakeSyncService,
                            timeProvider,
                            token);

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

    private async Task RunDispatchCleanupAsync(
        IDispatchCleanupService cleanupService,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now - _lastDispatchCleanupUtc < TimeSpan.FromHours(6))
        {
            return;
        }

        _lastDispatchCleanupUtc = now;
        try
        {
            await cleanupService.RunCleanupPassAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Dispatch cleanup pass failed.");
        }
    }

    private async Task RunDispatchRetryPassAsync(
        IDownloadRetryService downloadRetryService,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now - _lastDispatchRetryPassUtc < TimeSpan.FromMinutes(2))
        {
            return;
        }

        _lastDispatchRetryPassUtc = now;
        try
        {
            await downloadRetryService.RunRetryPassAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Dispatch retry pass failed.");
        }
    }

    private async Task PlanMetadataRefreshAutomationAsync(
        IJobScheduler jobScheduler,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        IReadOnlyList<Deluno.Jobs.Contracts.JobQueueItem> existingJobs,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now - _lastMetadataAutomationUtc < TimeSpan.FromHours(6))
        {
            return;
        }

        _lastMetadataAutomationUtc = now;
        var staleBefore = now.AddDays(-14);

        var queuedMovieIds = existingJobs
            .Where(job => job.JobType == "movies.metadata.refresh")
            .Select(job => job.RelatedEntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queuedSeriesIds = existingJobs
            .Where(job => job.JobType == "series.metadata.refresh")
            .Select(job => job.RelatedEntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var moviesToRefresh = (await movieCatalogRepository.ListAsync(cancellationToken))
            .Where(item => string.IsNullOrWhiteSpace(item.MetadataProviderId) || item.MetadataUpdatedUtc is null || item.MetadataUpdatedUtc < staleBefore)
            .Where(item => !queuedMovieIds.Contains(item.Id))
            .OrderBy(item => item.MetadataUpdatedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToArray();

        foreach (var movie in moviesToRefresh)
        {
            await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "movies.metadata.refresh",
                    Source: "metadata",
                    PayloadJson: JsonSerializer.Serialize(new { movie.Id, movie.Title, movie.ReleaseYear, scheduled = true }),
                    RelatedEntityType: "movie",
                    RelatedEntityId: movie.Id),
                cancellationToken);
        }

        var seriesToRefresh = (await seriesCatalogRepository.ListAsync(cancellationToken))
            .Where(item => string.IsNullOrWhiteSpace(item.MetadataProviderId) || item.MetadataUpdatedUtc is null || item.MetadataUpdatedUtc < staleBefore)
            .Where(item => !queuedSeriesIds.Contains(item.Id))
            .OrderBy(item => item.MetadataUpdatedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToArray();

        foreach (var series in seriesToRefresh)
        {
            await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "series.metadata.refresh",
                    Source: "metadata",
                    PayloadJson: JsonSerializer.Serialize(new { series.Id, series.Title, series.StartYear, scheduled = true }),
                    RelatedEntityType: "series",
                    RelatedEntityId: series.Id),
                cancellationToken);
        }
    }

    private async Task PlanIntakeAutomationAsync(
        IIntakeSyncService intakeSyncService,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now - _lastIntakeAutomationUtc < TimeSpan.FromMinutes(5))
        {
            return;
        }

        _lastIntakeAutomationUtc = now;
        await intakeSyncService.PlanDueSyncJobsAsync(cancellationToken);
    }

    private async Task PlanImportAutomationAsync(
        IJobScheduler jobScheduler,
        IJobQueueRepository jobQueueRepository,
        IPlatformSettingsRepository platformSettingsRepository,
        ILibrariesRepository librariesRepository,
        IQualityRepository qualityRepository,
        IDownloadClientTelemetryService downloadClientTelemetryService,
        IProcessorConnectionService processorConnectionService,
        IActivityFeedRepository activityFeedRepository,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now - _lastImportAutomationUtc < TimeSpan.FromSeconds(15))
        {
            return;
        }

        _lastImportAutomationUtc = now;

        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        if (libraries.Count == 0)
        {
            return;
        }

        var existingJobs = await jobQueueRepository.ListAsync(300, cancellationToken);
        var knownImportSources = existingJobs
            .Where(job => job.JobType == "filesystem.import.execute")
            .Select(job => TryReadImportSourcePath(job.PayloadJson))
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source => NormalizeSourceKey(source!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var recentWaiting = await activityFeedRepository.ListActivityAsync(150, null, null, cancellationToken);
        await ReconcileMatchedProcessorOutputsAsync(
            jobScheduler,
            platformSettingsRepository,
            activityFeedRepository,
            libraries,
            knownImportSources,
            cancellationToken);
        await RecordUnmatchedProcessorOutputsAsync(
            activityFeedRepository,
            movieCatalogRepository,
            seriesCatalogRepository,
            libraries,
            knownImportSources,
            recentWaiting,
            cancellationToken);
        await RecordProcessorTimeoutsAsync(
            platformSettingsRepository,
            activityFeedRepository,
            movieCatalogRepository,
            seriesCatalogRepository,
            libraries,
            recentWaiting,
            now,
            cancellationToken);

        var recentWaitingKeys = recentWaiting
            .Where(item => item.Category == "processing.waiting" && item.CreatedUtc > now.AddHours(-6))
            .Select(item => item.RelatedEntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var telemetry = await downloadClientTelemetryService.GetOverviewAsync(cancellationToken);
        await downloadClientTelemetryService.RunConfiguredHealthRemediationAsync(telemetry, cancellationToken);
        foreach (var item in telemetry.Clients.SelectMany(client => client.Queue))
        {
            if (item.Status is not ("importReady" or "completed"))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.SourcePath))
            {
                continue;
            }

            var sourceKey = NormalizeSourceKey(item.SourcePath);
            if (knownImportSources.Contains(sourceKey))
            {
                continue;
            }

            var library = ResolveLibraryForQueueItem(item, libraries);
            if (library is null)
            {
                continue;
            }

            if (string.Equals(library.ImportWorkflow, "refine-before-import", StringComparison.OrdinalIgnoreCase))
            {
                var handoff = await platformSettingsRepository.EnsureProcessorHandoffAsync(
                    new CreateProcessorHandoffRequest(
                        library.Id,
                        library.MediaType,
                        item.ClientId,
                        item.Id,
                        item.ReleaseName,
                        item.SourcePath,
                        library.ProcessorName),
                    cancellationToken);
                var currentHandoff = handoff;
                ProcessorSubmissionResult? submission = null;
                var connection = await platformSettingsRepository.FindProcessorConnectionByNameAsync(library.ProcessorName, cancellationToken);
                if (connection is { IsEnabled: true } && handoff.Status == "waiting")
                {
                    submission = await processorConnectionService.SubmitAsync(connection, handoff, cancellationToken);
                    currentHandoff = await platformSettingsRepository.UpdateProcessorHandoffAsync(
                        handoff.Id,
                        submission.Status,
                        null,
                        null,
                        submission.IsAccepted ? null : submission.Message,
                        cancellationToken) ?? handoff;
                    await platformSettingsRepository.RecordProcessorConnectionHealthAsync(
                        connection.Id,
                        submission.IsAccepted ? "healthy" : "degraded",
                        submission.Message,
                        cancellationToken);
                    await activityFeedRepository.RecordActivityAsync(
                        submission.IsAccepted ? "processing.submitted" : "processing.submission-failed",
                        submission.IsAccepted
                            ? $"Deluno submitted {item.Title} to {connection.Name}."
                            : $"Deluno could not submit {item.Title} to {connection.Name}. {submission.Message}",
                        JsonSerializer.Serialize(new
                        {
                            HandoffId = handoff.Id,
                            ConnectionId = connection.Id,
                            connection.Name,
                            connection.Provider,
                            submission.Status,
                            submission.StatusCode
                        }, PayloadJsonOptions),
                        null,
                        "processor-handoff",
                        handoff.Id,
                        cancellationToken);
                }
                var waitKey = handoff.Id;
                if (currentHandoff.Status != "failed" && recentWaitingKeys.Add(waitKey))
                {
                    var waitingMessage = submission?.IsAccepted == true
                        ? $"Deluno submitted {item.Title} to {connection!.Name} and is waiting for a cleaned output."
                        : $"{item.Title} is complete in {item.ClientName}; Deluno is waiting for {library.ProcessorName ?? "the configured processor"} to produce a cleaned output.";
                    await activityFeedRepository.RecordActivityAsync(
                        "processing.waiting",
                        waitingMessage,
                        JsonSerializer.Serialize(new
                        {
                            item.ClientId,
                            item.ClientName,
                            item.ReleaseName,
                            item.SourcePath,
                            HandoffId = handoff.Id,
                            library.Id,
                            library.Name,
                            library.ProcessorName,
                            library.ProcessorOutputPath,
                            library.ProcessorTimeoutMinutes
                        }, PayloadJsonOptions),
                        null,
                        "download",
                        waitKey,
                        cancellationToken);
                }

                continue;
            }

            var dispatchId = await jobQueueRepository.FindRecentDispatchIdAsync(
                item.ClientId,
                item.ReleaseName,
                cancellationToken);

            var request = new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: item.SourcePath,
                    FileName: InferImportFileName(item),
                    MediaType: item.MediaType,
                    Title: item.Title,
                    Year: InferYear(item.ReleaseName),
                    Genres: [],
                    Tags: string.IsNullOrWhiteSpace(item.Category) ? [] : [item.Category],
                    Studio: null,
                    OriginalLanguage: null),
                TransferMode: "auto",
                Overwrite: false,
                AllowCopyFallback: true,
                ForceReplacement: false,
                DispatchId: dispatchId);

            var job = await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "filesystem.import.execute",
                    Source: "download-client",
                    PayloadJson: JsonSerializer.Serialize(request, PayloadJsonOptions),
                    RelatedEntityType: library.MediaType == "tv" ? "series" : "movie",
                    RelatedEntityId: null),
                cancellationToken);

            knownImportSources.Add(sourceKey);

            await activityFeedRepository.RecordActivityAsync(
                "filesystem.import.auto-queued",
                $"{item.Title} finished in {item.ClientName}; Deluno queued it for import into {library.Name}.",
                JsonSerializer.Serialize(new
                {
                    item.ClientId,
                    item.ClientName,
                    item.ReleaseName,
                    item.SourcePath,
                    LibraryId = library.Id,
                    LibraryName = library.Name,
                    JobId = job.Id
                }, PayloadJsonOptions),
                job.Id,
                "library",
                library.Id,
                cancellationToken);
        }
    }

    private static async Task<string> ProcessJobAsync(
        Deluno.Jobs.Contracts.JobQueueItem job,
        IJobQueueRepository jobQueueRepository,
        IPlatformSettingsRepository platformSettingsRepository,
        ILibrariesRepository librariesRepository,
        IQualityRepository qualityRepository,
        IAcquisitionDecisionPipeline acquisitionPipeline,
        IDownloadClientGrabService downloadClientGrabService,
        IMetadataProvider metadataProvider,
        IImportPipelineService importPipelineService,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        IActivityFeedRepository activityFeedRepository,
        IIntakeSyncService intakeSyncService,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (job.JobType == "library.search")
        {
            var payload = ParseLibraryPayload(job.PayloadJson);
            if (payload is not null && !string.IsNullOrWhiteSpace(payload.LibraryName))
            {
                var now = timeProvider.GetUtcNow();
                var routing = await librariesRepository.GetLibraryRoutingAsync(payload.LibraryId, cancellationToken);
                var configuredSources = routing?.Sources.Count ?? 0;
                var configuredClients = routing?.DownloadClients.Count ?? 0;
                var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
                var library = libraries.FirstOrDefault(item => item.Id == payload.LibraryId);
                var customFormats = await ResolveCustomFormatsAsync(
                    qualityRepository,
                    library?.QualityProfileId,
                    cancellationToken);

                if (payload.MediaType == "movies")
                {
                    var ignoreRetryWindow = string.Equals(payload.TriggeredBy, "manual", StringComparison.OrdinalIgnoreCase);
                    var startedUtc = now;
                    var retryDelayed = ignoreRetryWindow
                        ? 0
                        : await movieCatalogRepository.CountRetryDelayedWantedAsync(payload.LibraryId, now, cancellationToken);
                    var candidates = (await movieCatalogRepository.ListEligibleWantedAsync(
                        payload.LibraryId,
                        payload.MaxItems,
                        now,
                        ignoreRetryWindow,
                        cancellationToken))
                        .Where(candidate => string.IsNullOrWhiteSpace(payload.TargetEntityId) || string.Equals(candidate.MovieId, payload.TargetEntityId, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    var matchedCount = 0;
                    var blockedCount = 0;
                    var checkedCount = 0;
                    var heldCount = 0;
                    var apiCallCount = 0;
                    long queuedReleaseBytes = 0;

                    foreach (var candidate in candidates)
                    {
                        if (!ignoreRetryWindow && await movieCatalogRepository.ConsumeSkipNextWantedSearchAsync(
                                candidate.MovieId,
                                payload.LibraryId,
                                cancellationToken))
                        {
                            var skippedNextEligibleUtc = now.AddHours(Math.Max(1, payload.RetryDelayHours));
                            await movieCatalogRepository.RecordSearchAttemptAsync(
                                candidate.MovieId,
                                payload.LibraryId,
                                payload.TriggeredBy,
                                "skipped",
                                now,
                                skippedNextEligibleUtc,
                                "Skipped one scheduled search by user request.",
                                null,
                                null,
                                null,
                                cancellationToken);
                            await jobQueueRepository.RecordSearchRetryWindowAsync(
                                "movie",
                                candidate.MovieId,
                                payload.LibraryId,
                                "movies",
                                NormalizeActionKind(candidate.WantedStatus),
                                skippedNextEligibleUtc,
                                now,
                                "skipped",
                                cancellationToken);
                            continue;
                        }

                        var decisionPlan = await acquisitionPipeline.PlanAsync(
                            new AcquisitionDecisionRequest(
                                candidate.Title,
                                candidate.ReleaseYear,
                                "movies",
                                candidate.CurrentQuality,
                                candidate.TargetQuality,
                                routing?.Sources ?? [],
                                routing?.DownloadClients ?? [],
                                customFormats),
                            cancellationToken);
                        if (decisionPlan.SourceCount > 0 && decisionPlan.DownloadClientCount > 0)
                        {
                            apiCallCount += decisionPlan.SourceCount;
                        }
                        var searchPlan = decisionPlan.SearchPlan;
                        var bestCandidate = searchPlan.BestCandidate;
                        var outcome = decisionPlan.Outcome;

                        if (outcome == "matched")
                        {
                            matchedCount++;
                        }
                        else if (outcome == "held")
                        {
                            heldCount++;
                        }
                        else if (outcome == "blocked")
                        {
                            blockedCount++;
                        }
                        else
                        {
                            checkedCount++;
                        }

                        if (decisionPlan.ShouldDispatch && decisionPlan.SelectedDownloadClient is not null && decisionPlan.DispatchRequest is not null)
                        {
                            var downloadClient = decisionPlan.SelectedDownloadClient;
                            var grabResult = await GrabBestCandidateAsync(
                                downloadClientGrabService,
                                downloadClient.DownloadClientId,
                                bestCandidate!,
                                decisionPlan.DispatchRequest,
                                cancellationToken);

                            await jobQueueRepository.RecordDownloadDispatchAsync(
                                payload.LibraryId,
                                "movies",
                                "movie",
                                candidate.MovieId,
                                bestCandidate!.ReleaseName,
                                bestCandidate.IndexerName,
                                downloadClient.DownloadClientId,
                                downloadClient.DownloadClientName,
                                grabResult.Status,
                                SerializeSearchPlan(searchPlan, grabResult),
                                grabResponseCode: grabResult.Succeeded ? 200 : 400,
                                grabFailureCode: null,
                                cancellationToken: cancellationToken);
                            if (bestCandidate?.SizeBytes is > 0)
                            {
                                queuedReleaseBytes += bestCandidate.SizeBytes.Value;
                            }
                        }

                        await movieCatalogRepository.RecordSearchAttemptAsync(
                            candidate.MovieId,
                            payload.LibraryId,
                            payload.TriggeredBy,
                            outcome,
                            now,
                            now.AddHours(Math.Max(1, payload.RetryDelayHours)),
                            decisionPlan.SearchResult,
                            bestCandidate?.ReleaseName,
                            bestCandidate?.IndexerName,
                            SerializeSearchPlan(searchPlan),
                            cancellationToken);

                        var nextEligibleUtc = now.AddHours(Math.Max(1, payload.RetryDelayHours));
                        await jobQueueRepository.RecordSearchRetryWindowAsync(
                            "movie",
                            candidate.MovieId,
                            payload.LibraryId,
                            "movies",
                            NormalizeActionKind(candidate.WantedStatus),
                            nextEligibleUtc,
                            now,
                            outcome,
                            cancellationToken);
                    }

                    await jobQueueRepository.RecordSearchCycleRunAsync(
                        new RecordSearchCycleRunRequest(
                            payload.LibraryId,
                            payload.LibraryName,
                            "movies",
                            payload.TriggeredBy,
                            candidates.Length > 0 || retryDelayed > 0 ? "completed" : "empty",
                            candidates.Length,
                            matchedCount,
                            retryDelayed,
                            SerializeCycleNotes(configuredSources, configuredClients, checkedCount, matchedCount, blockedCount, heldCount, retryDelayed, payload.MaxItems, apiCallCount, queuedReleaseBytes),
                            startedUtc,
                            timeProvider.GetUtcNow()),
                        cancellationToken);

                    await activityFeedRepository.RecordActivityAsync(
                        "library.search.executed",
                        FormatExecutionMessage(payload.LibraryName, candidates.Length, configuredSources, configuredClients, "movie"),
                        null,
                        job.Id,
                        "library",
                        payload.LibraryId,
                        cancellationToken);

                    return FormatCompletionMessage(payload.LibraryName, candidates.Length, configuredSources, configuredClients, "movie");
                }

                var seriesCandidates = (await seriesCatalogRepository.ListEligibleWantedAsync(
                    payload.LibraryId,
                    payload.MaxItems,
                    now,
                    string.Equals(payload.TriggeredBy, "manual", StringComparison.OrdinalIgnoreCase),
                    cancellationToken))
                    .Where(candidate => string.IsNullOrWhiteSpace(payload.TargetEntityId) || string.Equals(candidate.SeriesId, payload.TargetEntityId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var seriesIgnoreRetryWindow = string.Equals(payload.TriggeredBy, "manual", StringComparison.OrdinalIgnoreCase);
                var seriesStartedUtc = now;
                var seriesRetryDelayed = seriesIgnoreRetryWindow
                    ? 0
                    : await seriesCatalogRepository.CountRetryDelayedWantedAsync(payload.LibraryId, now, cancellationToken);
                var seriesMatchedCount = 0;
                var seriesBlockedCount = 0;
                var seriesCheckedCount = 0;
                var seriesHeldCount = 0;
                var seriesApiCallCount = 0;
                long seriesQueuedReleaseBytes = 0;

                foreach (var candidate in seriesCandidates)
                {
                    if (!seriesIgnoreRetryWindow && await seriesCatalogRepository.ConsumeSkipNextWantedSearchAsync(
                            candidate.SeriesId,
                            payload.LibraryId,
                            cancellationToken))
                    {
                        var skippedNextEligibleUtc = now.AddHours(Math.Max(1, payload.RetryDelayHours));
                        await seriesCatalogRepository.RecordSearchAttemptAsync(
                            candidate.SeriesId,
                            null,
                            payload.LibraryId,
                            payload.TriggeredBy,
                            "skipped",
                            now,
                            skippedNextEligibleUtc,
                            "Skipped one scheduled search by user request.",
                            null,
                            null,
                            null,
                            cancellationToken);
                        await jobQueueRepository.RecordSearchRetryWindowAsync(
                            "series",
                            candidate.SeriesId,
                            payload.LibraryId,
                            "tv",
                            NormalizeActionKind(candidate.WantedStatus),
                            skippedNextEligibleUtc,
                            now,
                            "skipped",
                            cancellationToken);
                        continue;
                    }

                    var decisionPlan = await acquisitionPipeline.PlanAsync(
                        new AcquisitionDecisionRequest(
                            candidate.Title,
                            candidate.StartYear,
                            "tv",
                            candidate.CurrentQuality,
                            candidate.TargetQuality,
                            routing?.Sources ?? [],
                            routing?.DownloadClients ?? [],
                            customFormats),
                        cancellationToken);
                    if (decisionPlan.SourceCount > 0 && decisionPlan.DownloadClientCount > 0)
                    {
                        seriesApiCallCount += decisionPlan.SourceCount;
                    }
                    var searchPlan = decisionPlan.SearchPlan;
                    var bestCandidate = searchPlan.BestCandidate;
                    var outcome = decisionPlan.Outcome;

                    if (outcome == "matched")
                    {
                        seriesMatchedCount++;
                    }
                    else if (outcome == "held")
                    {
                        seriesHeldCount++;
                    }
                    else if (outcome == "blocked")
                    {
                        seriesBlockedCount++;
                    }
                    else
                    {
                        seriesCheckedCount++;
                    }

                    if (decisionPlan.ShouldDispatch && decisionPlan.SelectedDownloadClient is not null && decisionPlan.DispatchRequest is not null)
                    {
                        var downloadClient = decisionPlan.SelectedDownloadClient;
                        var grabResult = await GrabBestCandidateAsync(
                            downloadClientGrabService,
                            downloadClient.DownloadClientId,
                            bestCandidate!,
                            decisionPlan.DispatchRequest,
                            cancellationToken);

                        await jobQueueRepository.RecordDownloadDispatchAsync(
                            payload.LibraryId,
                            "tv",
                            "series",
                            candidate.SeriesId,
                            bestCandidate!.ReleaseName,
                            bestCandidate.IndexerName,
                            downloadClient.DownloadClientId,
                            downloadClient.DownloadClientName,
                            grabResult.Status,
                            SerializeSearchPlan(searchPlan, grabResult),
                            grabResponseCode: grabResult.Succeeded ? 200 : 400,
                            grabFailureCode: null,
                            cancellationToken: cancellationToken);
                        if (bestCandidate?.SizeBytes is > 0)
                        {
                            seriesQueuedReleaseBytes += bestCandidate.SizeBytes.Value;
                        }
                    }

                    await seriesCatalogRepository.RecordSearchAttemptAsync(
                        candidate.SeriesId,
                        null,
                        payload.LibraryId,
                        payload.TriggeredBy,
                        outcome,
                        now,
                        now.AddHours(Math.Max(1, payload.RetryDelayHours)),
                        decisionPlan.SearchResult,
                        bestCandidate?.ReleaseName,
                        bestCandidate?.IndexerName,
                        SerializeSearchPlan(searchPlan),
                        cancellationToken);

                    var nextEligibleUtc = now.AddHours(Math.Max(1, payload.RetryDelayHours));
                    await jobQueueRepository.RecordSearchRetryWindowAsync(
                        "series",
                        candidate.SeriesId,
                        payload.LibraryId,
                        "tv",
                        NormalizeActionKind(candidate.WantedStatus),
                        nextEligibleUtc,
                        now,
                        outcome,
                        cancellationToken);
                }

                await jobQueueRepository.RecordSearchCycleRunAsync(
                    new RecordSearchCycleRunRequest(
                        payload.LibraryId,
                        payload.LibraryName,
                        "tv",
                        payload.TriggeredBy,
                        seriesCandidates.Length > 0 || seriesRetryDelayed > 0 ? "completed" : "empty",
                        seriesCandidates.Length,
                        seriesMatchedCount,
                        seriesRetryDelayed,
                        SerializeCycleNotes(configuredSources, configuredClients, seriesCheckedCount, seriesMatchedCount, seriesBlockedCount, seriesHeldCount, seriesRetryDelayed, payload.MaxItems, seriesApiCallCount, seriesQueuedReleaseBytes),
                        seriesStartedUtc,
                        timeProvider.GetUtcNow()),
                    cancellationToken);

                await activityFeedRepository.RecordActivityAsync(
                    "library.search.executed",
                    FormatExecutionMessage(payload.LibraryName, seriesCandidates.Length, configuredSources, configuredClients, "TV show"),
                    null,
                    job.Id,
                    "library",
                    payload.LibraryId,
                    cancellationToken);

                return FormatCompletionMessage(payload.LibraryName, seriesCandidates.Length, configuredSources, configuredClients, "TV show");
            }

            return "Finished checking a library.";
        }

        if (job.JobType == "episode.search")
        {
            var payload = ParseEpisodeSearchPayload(job.PayloadJson);
            if (payload is not null && !string.IsNullOrWhiteSpace(payload.EpisodeId))
            {
                var now = timeProvider.GetUtcNow();
                var routing = await librariesRepository.GetLibraryRoutingAsync(payload.LibraryId, cancellationToken);
                var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
                var library = libraries.FirstOrDefault(item => item.Id == payload.LibraryId);
                var customFormats = await ResolveCustomFormatsAsync(
                    qualityRepository,
                    library?.QualityProfileId,
                    cancellationToken);
                var targetQuality = await seriesCatalogRepository.GetEpisodeTargetQualityAsync(
                    payload.EpisodeId,
                    payload.LibraryId,
                    cancellationToken);
                var currentQuality = await seriesCatalogRepository.GetEpisodeCurrentQualityAsync(
                    payload.EpisodeId,
                    cancellationToken);

                var decisionPlan = await acquisitionPipeline.PlanAsync(
                    new AcquisitionDecisionRequest(
                        Title: payload.Title,
                        Year: null,
                        MediaType: "tv",
                        CurrentQuality: currentQuality,
                        TargetQuality: targetQuality,
                        Sources: routing?.Sources ?? [],
                        DownloadClients: routing?.DownloadClients ?? [],
                        CustomFormats: customFormats,
                        SeasonNumber: payload.SeasonNumber,
                        EpisodeNumber: payload.EpisodeNumber),
                    cancellationToken);

                var searchPlan = decisionPlan.SearchPlan;
                var bestCandidate = searchPlan.BestCandidate;
                var outcome = decisionPlan.Outcome;

                if (decisionPlan.ShouldDispatch && decisionPlan.SelectedDownloadClient is not null && decisionPlan.DispatchRequest is not null)
                {
                    var downloadClient = decisionPlan.SelectedDownloadClient;
                    var grabResult = await GrabBestCandidateAsync(
                        downloadClientGrabService,
                        downloadClient.DownloadClientId,
                        bestCandidate!,
                        decisionPlan.DispatchRequest,
                        cancellationToken);

                    await jobQueueRepository.RecordDownloadDispatchAsync(
                        payload.LibraryId,
                        "tv",
                        "episode",
                        payload.EpisodeId,
                        bestCandidate!.ReleaseName,
                        bestCandidate.IndexerName,
                        downloadClient.DownloadClientId,
                        downloadClient.DownloadClientName,
                        grabResult.Status,
                        SerializeSearchPlan(searchPlan, grabResult),
                        grabResponseCode: grabResult.Succeeded ? 200 : 400,
                        grabFailureCode: null,
                        cancellationToken: cancellationToken);
                }

                await seriesCatalogRepository.RecordSearchAttemptAsync(
                    payload.SeriesId,
                    payload.EpisodeId,
                    payload.LibraryId,
                    "automatic",
                    outcome,
                    now,
                    now.AddDays(1),
                    decisionPlan.SearchResult,
                    bestCandidate?.ReleaseName,
                    bestCandidate?.IndexerName,
                    SerializeSearchPlan(searchPlan),
                    cancellationToken);

                await activityFeedRepository.RecordActivityAsync(
                    "episode.search.executed",
                    $"Episode search executed: S{payload.SeasonNumber:D2}E{payload.EpisodeNumber:D2} - {outcome}",
                    null,
                    job.Id,
                    "episode",
                    payload.EpisodeId,
                    cancellationToken);

                return $"Finished searching for episode S{payload.SeasonNumber:D2}E{payload.EpisodeNumber:D2}.";
            }

            return "Finished searching for episode.";
        }

        return job.JobType switch
        {
            "intake.sync" => await RunIntakeSyncAsync(job, intakeSyncService, cancellationToken),
            "movies.metadata.refresh" => await RefreshMovieMetadataAsync(job, metadataProvider, movieCatalogRepository, activityFeedRepository, cancellationToken),
            "series.metadata.refresh" => await RefreshSeriesMetadataAsync(job, metadataProvider, seriesCatalogRepository, activityFeedRepository, cancellationToken),
            "filesystem.import.execute" => await ExecuteImportJobAsync(job, importPipelineService, cancellationToken),
            "movies.quality.recalculate" => await RecalculateMovieQualityAsync(job, movieCatalogRepository, activityFeedRepository, stoppingToken: cancellationToken),
            "series.quality.recalculate" => await RecalculateSeriesQualityAsync(job, seriesCatalogRepository, activityFeedRepository, stoppingToken: cancellationToken),
            "movies.catalog.refresh" => "Finished checking your movie library.",
            "series.catalog.refresh" => "Finished checking your TV show library.",
            _ => "Finished a background task."
        };
    }

    private static async Task<string> RunIntakeSyncAsync(
        Deluno.Jobs.Contracts.JobQueueItem job,
        IIntakeSyncService intakeSyncService,
        CancellationToken cancellationToken)
    {
        var payload = ParseIntakeSyncPayload(job.PayloadJson);
        var sourceId = payload?.SourceId ?? job.RelatedEntityId;
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return "Skipped intake sync because no source id was provided.";
        }

        var result = await intakeSyncService.RunAsync(sourceId, job.Id, payload?.Manual == true, cancellationToken);
        return $"Intake sync completed for {result.SourceName}: {result.Summary}";
    }

    private static async Task RecordUnmatchedProcessorOutputsAsync(
        IActivityFeedRepository activityFeedRepository,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        IReadOnlyList<LibraryItem> libraries,
        ISet<string> knownImportSources,
        IReadOnlyList<ActivityEventItem> recentActivity,
        CancellationToken cancellationToken)
    {
        var reportedOutputKeys = recentActivity
            .Where(item => item.Category == "processing.output.unmatched")
            .Select(item => item.RelatedEntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var refineLibraries = libraries
            .Where(library =>
                string.Equals(library.ImportWorkflow, "refine-before-import", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(library.ProcessorOutputPath) &&
                Directory.Exists(library.ProcessorOutputPath))
            .ToArray();

        foreach (var library in refineLibraries)
        {
            IReadOnlyList<string> files;
            try
            {
                files = Directory
                    .EnumerateFiles(library.ProcessorOutputPath!, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(IsImportableVideoFile)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(10)
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await activityFeedRepository.RecordActivityAsync(
                    "processing.output.scan-failed",
                    $"Deluno could not scan the processor output folder for {library.Name}. Check the path and service permissions.",
                    JsonSerializer.Serialize(new
                    {
                        LibraryId = library.Id,
                        LibraryName = library.Name,
                        library.ProcessorOutputPath,
                        Error = ex.Message
                    }, PayloadJsonOptions),
                    null,
                    "library",
                    library.Id,
                    cancellationToken);
                continue;
            }

            foreach (var file in files)
            {
                var sourceKey = NormalizeSourceKey(file);
                if (knownImportSources.Contains(sourceKey))
                {
                    continue;
                }

                var outputKey = $"{library.Id}:{Path.GetFileName(file).ToLowerInvariant()}";
                if (!reportedOutputKeys.Add(outputKey))
                {
                    continue;
                }

                var title = Path.GetFileNameWithoutExtension(file);
                var summary = $"Deluno found a cleaned output in {library.Name}, but cannot safely match it to a download hand-off.";
                var recommendation = "Use the processor callback with Deluno's hand-off ID, or review this file and import it manually. Deluno did not import it automatically.";
                if (library.MediaType == "tv")
                {
                    await seriesCatalogRepository.AddImportRecoveryCaseAsync(
                        new CreateSeriesImportRecoveryCaseRequest(title, "processor-unmatched-output", summary, recommendation, JsonSerializer.Serialize(new { LibraryId = library.Id, FileName = Path.GetFileName(file) }, PayloadJsonOptions)),
                        cancellationToken);
                }
                else
                {
                    await movieCatalogRepository.AddImportRecoveryCaseAsync(
                        new CreateMovieImportRecoveryCaseRequest(title, "processor-unmatched-output", summary, recommendation, JsonSerializer.Serialize(new { LibraryId = library.Id, FileName = Path.GetFileName(file) }, PayloadJsonOptions)),
                        cancellationToken);
                }
                await activityFeedRepository.RecordActivityAsync(
                    "processing.output.unmatched",
                    summary,
                    JsonSerializer.Serialize(new
                    {
                        LibraryId = library.Id,
                        LibraryName = library.Name,
                        library.MediaType,
                        FileName = Path.GetFileName(file),
                        Recommendation = recommendation
                    }, PayloadJsonOptions),
                    null,
                    "processor-output",
                    outputKey,
                    cancellationToken);
            }
        }
    }

    /// <summary>
    /// Completes the processor-agnostic path. A processor does not need to call
    /// Deluno or use a vendor adapter: it writes its result below the configured
    /// processed-output root while retaining the final source-folder name. That
    /// one stable path component lets Deluno match the output to a durable
    /// hand-off without guessing from a release title.
    /// </summary>
    private static async Task ReconcileMatchedProcessorOutputsAsync(
        IJobScheduler jobScheduler,
        IPlatformSettingsRepository platformSettingsRepository,
        IActivityFeedRepository activityFeedRepository,
        IReadOnlyList<LibraryItem> libraries,
        ISet<string> knownImportSources,
        CancellationToken cancellationToken)
    {
        var waitingStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "waiting", "submitted", "accepted", "started"
        };
        var handoffs = await platformSettingsRepository.ListProcessorHandoffsAsync(null, 250, cancellationToken);

        foreach (var handoff in handoffs.Where(item => waitingStatuses.Contains(item.Status)))
        {
            var library = libraries.FirstOrDefault(item =>
                string.Equals(item.Id, handoff.LibraryId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ImportWorkflow, "refine-before-import", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(item.ProcessorOutputPath));
            if (library is null)
            {
                continue;
            }

            var candidates = FindCorrelatedProcessorOutputs(library.ProcessorOutputPath!, handoff.SourcePath)
                .Where(path => !knownImportSources.Contains(NormalizeSourceKey(path)))
                .ToArray();
            if (candidates.Length != 1)
            {
                continue;
            }

            var outputPath = candidates[0];
            var importPayload = new
            {
                preview = new
                {
                    sourcePath = outputPath,
                    fileName = Path.GetFileName(outputPath),
                    mediaType = library.MediaType,
                    title = Path.GetFileNameWithoutExtension(outputPath),
                    year = (int?)null,
                    genres = Array.Empty<string>(),
                    tags = new[] { "processed" },
                    studio = (string?)null,
                    originalLanguage = (string?)null
                },
                transferMode = "auto",
                overwrite = false,
                allowCopyFallback = true,
                forceReplacement = false
            };

            var importJob = await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "filesystem.import.execute",
                    Source: "processor-output-watch",
                    PayloadJson: JsonSerializer.Serialize(importPayload),
                    RelatedEntityType: library.MediaType == "tv" ? "series" : "movie",
                    RelatedEntityId: null,
                    IdempotencyKey: $"processor-output:{library.Id}:{Path.GetFullPath(outputPath).ToLowerInvariant()}"),
                cancellationToken);

            await platformSettingsRepository.UpdateProcessorHandoffAsync(
                handoff.Id,
                "completed",
                outputPath,
                importJob.Id,
                null,
                cancellationToken);
            knownImportSources.Add(NormalizeSourceKey(outputPath));
            await activityFeedRepository.RecordActivityAsync(
                "processing.output.matched.import-queued",
                $"Deluno matched processed output for {handoff.ReleaseName} and queued it for import.",
                JsonSerializer.Serialize(new
                {
                    HandoffId = handoff.Id,
                    library.Id,
                    library.Name,
                    SourcePath = handoff.SourcePath,
                    OutputPath = outputPath,
                    JobId = importJob.Id,
                    Match = "stable processed-output subfolder"
                }, PayloadJsonOptions),
                importJob.Id,
                "processor-handoff",
                handoff.Id,
                cancellationToken);
        }
    }

    private static IReadOnlyList<string> FindCorrelatedProcessorOutputs(string outputRoot, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(outputRoot) || string.IsNullOrWhiteSpace(sourcePath))
        {
            return [];
        }

        try
        {
            var root = Path.GetFullPath(outputRoot);
            if (!Directory.Exists(root))
            {
                return [];
            }

            var sourceLeaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourcePath));
            if (string.IsNullOrWhiteSpace(sourceLeaf) || sourceLeaf is "." or "..")
            {
                return [];
            }

            var directoryNames = new[]
            {
                sourceLeaf,
                Path.GetFileNameWithoutExtension(sourceLeaf)
            }
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var matches = new List<string>();
            foreach (var directoryName in directoryNames)
            {
                var expectedDirectory = Path.GetFullPath(Path.Combine(root, directoryName));
                if (!expectedDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !Directory.Exists(expectedDirectory))
                {
                    continue;
                }

                matches.AddRange(Directory.EnumerateFiles(expectedDirectory, "*.*", SearchOption.AllDirectories)
                    .Where(IsImportableVideoFile)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(2));
            }

            return matches.Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    private static async Task RecordProcessorTimeoutsAsync(
        IPlatformSettingsRepository platformSettingsRepository,
        IActivityFeedRepository activityFeedRepository,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        IReadOnlyList<LibraryItem> libraries,
        IReadOnlyList<ActivityEventItem> recentActivity,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var timeoutKeys = recentActivity
            .Where(item => item.Category == "processing.timeout")
            .Select(item => item.RelatedEntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var waiting in recentActivity.Where(item => item.Category == "processing.waiting"))
        {
            if (string.IsNullOrWhiteSpace(waiting.RelatedEntityId) ||
                timeoutKeys.Contains(waiting.RelatedEntityId))
            {
                continue;
            }

            var details = TryReadProcessingWaitDetails(waiting.DetailsJson);
            var library = !string.IsNullOrWhiteSpace(details.LibraryId)
                ? libraries.FirstOrDefault(item => string.Equals(item.Id, details.LibraryId, StringComparison.OrdinalIgnoreCase))
                : null;
            if (library is null)
            {
                continue;
            }

            var timeout = TimeSpan.FromMinutes(Math.Max(1, library.ProcessorTimeoutMinutes));
            if (now - waiting.CreatedUtc < timeout)
            {
                continue;
            }

            var title = details.ReleaseName ?? details.SourcePath ?? "Processor output";
            var summary = $"{title} waited longer than {library.ProcessorTimeoutMinutes} minutes for a cleaned processor output.";
            var recommended = library.ProcessorFailureMode switch
            {
                "import-original" => "Review the original download, then manually queue import if it is acceptable.",
                "manual-review" => "Open Queue recovery and choose retry, manual import, or dismiss.",
                _ => "Check the processor logs and output folder, then retry once the cleaned file exists."
            };

            if (library.MediaType == "tv")
            {
                await seriesCatalogRepository.AddImportRecoveryCaseAsync(
                    new CreateSeriesImportRecoveryCaseRequest(title, "processor-timeout", summary, recommended, waiting.DetailsJson),
                    cancellationToken);
            }
            else
            {
                await movieCatalogRepository.AddImportRecoveryCaseAsync(
                    new CreateMovieImportRecoveryCaseRequest(title, "processor-timeout", summary, recommended, waiting.DetailsJson),
                    cancellationToken);
            }

            await platformSettingsRepository.UpdateProcessorHandoffAsync(
                waiting.RelatedEntityId,
                "timed-out",
                null,
                null,
                summary,
                cancellationToken);

            await activityFeedRepository.RecordActivityAsync(
                "processing.timeout",
                summary,
                waiting.DetailsJson,
                null,
                "download",
                waiting.RelatedEntityId,
                cancellationToken);
        }
    }

    private static bool IsImportableVideoFile(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".mkv" or ".mp4" or ".avi" or ".mov" or ".m4v";

    private static ProcessingWaitDetails TryReadProcessingWaitDetails(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return new ProcessingWaitDetails(null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            var root = document.RootElement;
            return new ProcessingWaitDetails(
                TryGetProperty(root, "libraryId", out var libraryId) && libraryId.ValueKind == JsonValueKind.String ? libraryId.GetString() : null,
                TryGetProperty(root, "releaseName", out var releaseName) && releaseName.ValueKind == JsonValueKind.String ? releaseName.GetString() : null,
                TryGetProperty(root, "sourcePath", out var sourcePath) && sourcePath.ValueKind == JsonValueKind.String ? sourcePath.GetString() : null);
        }
        catch (JsonException)
        {
            return new ProcessingWaitDetails(null, null, null);
        }
    }

    private static async Task<string> RecalculateMovieQualityAsync(
        Deluno.Jobs.Contracts.JobQueueItem job,
        IMovieCatalogRepository movieCatalogRepository,
        IActivityFeedRepository activityFeedRepository,
        CancellationToken stoppingToken)
    {
        var payload = ParseQualityPayload(job.PayloadJson);
        if (payload is null)
        {
            return "Finished refreshing movie quality decisions.";
        }

        var updated = await movieCatalogRepository.ReevaluateLibraryWantedStateAsync(
            payload.LibraryId,
            payload.CutoffQuality,
            payload.UpgradeUntilCutoff,
            payload.UpgradeUnknownItems,
            stoppingToken);

        await activityFeedRepository.RecordActivityAsync(
            "library.quality.recalculated",
            $"Deluno refreshed quality decisions for {payload.LibraryName} across {updated} movie record{(updated == 1 ? "" : "s")}.",
            null,
            job.Id,
            "library",
            payload.LibraryId,
            stoppingToken);

        return $"Finished refreshing quality decisions for {payload.LibraryName}.";
    }

    private static async Task<string> ExecuteImportJobAsync(
        Deluno.Jobs.Contracts.JobQueueItem job,
        IImportPipelineService importPipelineService,
        CancellationToken stoppingToken)
    {
        var payload = ParseImportPayload(job.PayloadJson);
        if (payload is null)
        {
            throw new InvalidOperationException("Import job payload could not be read.");
        }

        var result = await importPipelineService.ExecuteAsync(payload, stoppingToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.Message);
        }

        return result.Response?.Message ?? "Import completed.";
    }

    private static async Task<string> RefreshMovieMetadataAsync(
        Deluno.Jobs.Contracts.JobQueueItem job,
        IMetadataProvider metadataProvider,
        IMovieCatalogRepository movieCatalogRepository,
        IActivityFeedRepository activityFeedRepository,
        CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(job.RelatedEntityId))
        {
            return "Movie metadata refresh skipped because no movie was linked.";
        }

        var movie = await movieCatalogRepository.GetByIdAsync(job.RelatedEntityId, stoppingToken);
        if (movie is null)
        {
            return "Movie metadata refresh skipped because the movie no longer exists.";
        }

        var matches = await metadataProvider.SearchAsync(
            new MetadataLookupRequest(movie.Title, "movies", movie.ReleaseYear, movie.MetadataProviderId),
            stoppingToken);
        var match = matches.FirstOrDefault();
        if (match is null)
        {
            return $"No metadata match found for {movie.Title}.";
        }

        await movieCatalogRepository.UpdateMetadataAsync(
            movie.Id,
            match.Provider,
            match.ProviderId,
            match.OriginalTitle,
            match.Overview,
            match.PosterUrl,
            match.BackdropUrl,
            match.Rating,
            string.Join(", ", match.Genres),
            match.ExternalUrl,
            match.ImdbId,
            JsonSerializer.Serialize(match, PayloadJsonOptions),
            stoppingToken);

        await activityFeedRepository.RecordActivityAsync(
            "metadata.movie.refreshed",
            $"{movie.Title} metadata was refreshed by the background worker.",
            JsonSerializer.Serialize(match, PayloadJsonOptions),
            job.Id,
            "movie",
            movie.Id,
            stoppingToken);

        return $"Refreshed metadata for {movie.Title}.";
    }

    private static async Task<string> RefreshSeriesMetadataAsync(
        Deluno.Jobs.Contracts.JobQueueItem job,
        IMetadataProvider metadataProvider,
        ISeriesCatalogRepository seriesCatalogRepository,
        IActivityFeedRepository activityFeedRepository,
        CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(job.RelatedEntityId))
        {
            return "TV metadata refresh skipped because no series was linked.";
        }

        var series = await seriesCatalogRepository.GetByIdAsync(job.RelatedEntityId, stoppingToken);
        if (series is null)
        {
            return "TV metadata refresh skipped because the series no longer exists.";
        }

        var matches = await metadataProvider.SearchAsync(
            new MetadataLookupRequest(series.Title, "tv", series.StartYear, series.MetadataProviderId),
            stoppingToken);
        var match = matches.FirstOrDefault();
        if (match is null)
        {
            return $"No metadata match found for {series.Title}.";
        }

        await seriesCatalogRepository.UpdateMetadataAsync(
            series.Id,
            match.Provider,
            match.ProviderId,
            match.OriginalTitle,
            match.Overview,
            match.PosterUrl,
            match.BackdropUrl,
            match.Rating,
            string.Join(", ", match.Genres),
            match.ExternalUrl,
            match.ImdbId,
            JsonSerializer.Serialize(match, PayloadJsonOptions),
            stoppingToken);

        // Re-syncing the catalogue on the schedule is how an episode announced
        // after the show was added ever becomes known. Without it the inventory
        // is only as current as the day someone last pressed Refresh.
        var catalogue = await SyncSeriesCatalogueAsync(
            metadataProvider,
            seriesCatalogRepository,
            series.Id,
            match.ProviderId,
            stoppingToken);

        await activityFeedRepository.RecordActivityAsync(
            "metadata.series.refreshed",
            $"{series.Title} metadata was refreshed by the background worker.",
            JsonSerializer.Serialize(match, PayloadJsonOptions),
            job.Id,
            "series",
            series.Id,
            stoppingToken);

        if (catalogue.AddedCount > 0)
        {
            await activityFeedRepository.RecordActivityAsync(
                "metadata.series.catalogue",
                $"Deluno learned {catalogue.AddedCount} more episode{(catalogue.AddedCount == 1 ? "" : "s")} of {series.Title}.",
                JsonSerializer.Serialize(catalogue, PayloadJsonOptions),
                job.Id,
                "series",
                series.Id,
                stoppingToken);

            return $"Refreshed metadata for {series.Title} and added {catalogue.AddedCount} newly announced episode{(catalogue.AddedCount == 1 ? "" : "s")}.";
        }

        return $"Refreshed metadata for {series.Title}.";
    }

    /// <summary>
    /// Pull the provider's season/episode list into the inventory. A provider
    /// that cannot answer leaves the catalogue as it was — never a failed job.
    /// </summary>
    private static async Task<SeriesCatalogueSyncResult> SyncSeriesCatalogueAsync(
        IMetadataProvider metadataProvider,
        ISeriesCatalogRepository seriesCatalogRepository,
        string seriesId,
        string? providerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return SeriesCatalogueSyncResult.None;
        }

        IReadOnlyList<MetadataSeason> seasons;
        try
        {
            seasons = await metadataProvider.GetSeriesCatalogueAsync(providerId, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return SeriesCatalogueSyncResult.None;
        }

        var episodes = seasons
            .SelectMany(season => season.Episodes)
            .Select(episode => new CatalogueEpisodeItem(
                episode.SeasonNumber,
                episode.EpisodeNumber,
                episode.Title,
                episode.Overview,
                episode.AirDateUtc))
            .ToArray();

        return episodes.Length == 0
            ? SeriesCatalogueSyncResult.None
            : await seriesCatalogRepository.SyncEpisodeCatalogueAsync(seriesId, episodes, "tmdb", cancellationToken);
    }

    private static async Task<string> RecalculateSeriesQualityAsync(
        Deluno.Jobs.Contracts.JobQueueItem job,
        ISeriesCatalogRepository seriesCatalogRepository,
        IActivityFeedRepository activityFeedRepository,
        CancellationToken stoppingToken)
    {
        var payload = ParseQualityPayload(job.PayloadJson);
        if (payload is null)
        {
            return "Finished refreshing TV quality decisions.";
        }

        var updated = await seriesCatalogRepository.ReevaluateLibraryWantedStateAsync(
            payload.LibraryId,
            payload.CutoffQuality,
            payload.UpgradeUntilCutoff,
            payload.UpgradeUnknownItems,
            stoppingToken);

        await activityFeedRepository.RecordActivityAsync(
            "library.quality.recalculated",
            $"Deluno refreshed quality decisions for {payload.LibraryName} across {updated} TV show record{(updated == 1 ? "" : "s")}.",
            null,
            job.Id,
            "library",
            payload.LibraryId,
            stoppingToken);

        return $"Finished refreshing quality decisions for {payload.LibraryName}.";
    }

    private static string FormatExecutionMessage(
        string libraryName,
        int candidateCount,
        int sourceCount,
        int clientCount,
        string mediaLabel)
    {
        if (candidateCount == 0)
        {
            return $"Deluno checked {libraryName} and found nothing else to look for right now.";
        }

        if (sourceCount == 0)
        {
            return $"Deluno found {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} to search in {libraryName}, but this library does not have any indexers linked yet.";
        }

        if (clientCount == 0)
        {
            return $"Deluno found {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} to search in {libraryName}, but it still needs a download client for this library.";
        }

        return $"Deluno checked {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} in {libraryName} using {sourceCount} source{(sourceCount == 1 ? "" : "s")}.";
    }

    private static string FormatCompletionMessage(
        string libraryName,
        int candidateCount,
        int sourceCount,
        int clientCount,
        string mediaLabel)
    {
        if (candidateCount == 0)
        {
            return $"Finished checking {libraryName}. Nothing else needs attention right now.";
        }

        if (sourceCount == 0)
        {
            return $"Finished checking {libraryName}. Deluno found {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} but this library still needs indexers.";
        }

        if (clientCount == 0)
        {
            return $"Finished checking {libraryName}. Deluno found {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} but this library still needs a download client.";
        }

        return $"Finished checking {libraryName}. Deluno reviewed {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} for new or better releases.";
    }

    private static async Task<DownloadClientGrabResult> GrabBestCandidateAsync(
        IDownloadClientGrabService downloadClientGrabService,
        string downloadClientId,
        MediaSearchCandidate candidate,
        DownloadClientGrabRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidate.DownloadUrl))
        {
            return new DownloadClientGrabResult(
                downloadClientId,
                candidate.ReleaseName,
                false,
                "planned",
                "No download URL was available.");
        }

        return await downloadClientGrabService.GrabAsync(
            downloadClientId,
            request,
            cancellationToken);
    }

    private static string? SerializeSearchPlan(MediaSearchPlan plan, DownloadClientGrabResult? grabResult = null)
    {
        if (plan.Candidates.Count == 0)
        {
            return null;
        }

        return grabResult is null
            ? JsonSerializer.Serialize(plan, PayloadJsonOptions)
            : JsonSerializer.Serialize(new { searchPlan = plan, grabResult }, PayloadJsonOptions);
    }

    private static string SerializeCycleNotes(
        int configuredSources,
        int configuredClients,
        int checkedCount,
        int matchedCount,
        int blockedCount,
        int heldCount,
        int retryDelayedCount,
        int maxItems,
        int apiCallCount,
        long queuedReleaseBytes)
    {
        return JsonSerializer.Serialize(new
        {
            configuredSources,
            configuredClients,
            checkedCount,
            matchedCount,
            blockedCount,
            heldCount,
            retryDelayedCount,
            maxItems,
            apiCallCount,
            queuedReleaseBytes
        }, PayloadJsonOptions);
    }

    private static string NormalizeActionKind(string? wantedStatus)
        => string.Equals(wantedStatus, "upgrade", StringComparison.OrdinalIgnoreCase)
            ? "upgrade"
            : "missing";

    private static async Task<IReadOnlyList<CustomFormatItem>> ResolveCustomFormatsAsync(
        IQualityRepository repository,
        string? qualityProfileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(qualityProfileId))
        {
            return [];
        }

        var profiles = await repository.ListQualityProfilesAsync(cancellationToken);
        var profile = profiles.FirstOrDefault(item => item.Id == qualityProfileId);
        if (profile is null || string.IsNullOrWhiteSpace(profile.CustomFormatIds))
        {
            return [];
        }

        var ids = profile.CustomFormatIds
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length == 0)
        {
            return [];
        }

        var formats = await repository.ListCustomFormatsAsync(cancellationToken);
        return formats.Where(item => ids.Contains(item.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    private static LibrarySearchPayload? ParseLibraryPayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<LibrarySearchPayload>(payloadJson ?? "{}", PayloadJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static LibraryQualityPayload? ParseQualityPayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<LibraryQualityPayload>(payloadJson ?? "{}", PayloadJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static ImportExecuteRequest? ParseImportPayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<ImportExecuteRequest>(payloadJson ?? "{}", PayloadJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static EpisodeSearchPayload? ParseEpisodeSearchPayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<EpisodeSearchPayload>(payloadJson ?? "{}", PayloadJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static IntakeSyncPayload? ParseIntakeSyncPayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<IntakeSyncPayload>(payloadJson ?? "{}", PayloadJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static LibraryItem? ResolveLibraryForQueueItem(DownloadQueueItem item, IReadOnlyList<LibraryItem> libraries)
    {
        if (!string.IsNullOrWhiteSpace(item.LibraryId))
        {
            var assignedLibrary = libraries.FirstOrDefault(library =>
                string.Equals(library.Id, item.LibraryId, StringComparison.OrdinalIgnoreCase));
            if (assignedLibrary is not null)
            {
                return assignedLibrary;
            }
        }

        var normalizedMediaType = item.MediaType.Equals("tv", StringComparison.OrdinalIgnoreCase) ||
            item.MediaType.Equals("series", StringComparison.OrdinalIgnoreCase)
            ? "tv"
            : "movies";
        var mediaLibraries = libraries
            .Where(library => string.Equals(library.MediaType, normalizedMediaType, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (!string.IsNullOrWhiteSpace(item.SourcePath))
        {
            var source = NormalizeSourceKey(item.SourcePath);
            var pathMatch = mediaLibraries.FirstOrDefault(library =>
                !string.IsNullOrWhiteSpace(library.DownloadsPath) &&
                source.StartsWith(NormalizeSourceKey(library.DownloadsPath), StringComparison.OrdinalIgnoreCase));
            if (pathMatch is not null)
            {
                return pathMatch;
            }
        }

        return mediaLibraries.FirstOrDefault();
    }

    private static string? TryReadImportSourcePath(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (!TryGetProperty(root, "preview", out var preview) ||
                !TryGetProperty(preview, "sourcePath", out var sourcePath) ||
                sourcePath.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return sourcePath.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string NormalizeSourceKey(string value)
        => value.Trim().TrimEnd('\\', '/').Replace('\\', '/');

    private static string InferImportFileName(DownloadQueueItem item)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(item.ReleaseName
            .Select(character => invalid.Contains(character) ? '.' : character)
            .ToArray())
            .Replace(' ', '.')
            .Trim('.');

        while (cleaned.Contains("..", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("..", ".", StringComparison.Ordinal);
        }

        if (cleaned.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
            cleaned.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
            cleaned.EndsWith(".avi", StringComparison.OrdinalIgnoreCase) ||
            cleaned.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ||
            cleaned.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase))
        {
            return cleaned;
        }

        return $"{(string.IsNullOrWhiteSpace(cleaned) ? item.Id : cleaned)}.mkv";
    }

    private static int? InferYear(string value)
    {
        var parts = value.Split([' ', '.', '-', '_', '[', ']', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.Length == 4 &&
                int.TryParse(part, out var year) &&
                year is >= 1900 and <= 2100)
            {
                return year;
            }
        }

        return null;
    }

    private sealed record LibrarySearchPayload(
        string LibraryId,
        string LibraryName,
        string MediaType,
        bool CheckMissing,
        bool CheckUpgrades,
        int MaxItems,
        int RetryDelayHours,
        string TriggeredBy,
        string? TargetEntityId = null);

    private sealed record LibraryQualityPayload(
        string LibraryId,
        string LibraryName,
        string MediaType,
        string? CutoffQuality,
        bool UpgradeUntilCutoff,
        bool UpgradeUnknownItems);

    private sealed record EpisodeSearchPayload(
        string EpisodeId,
        string SeriesId,
        string LibraryId,
        int SeasonNumber,
        int EpisodeNumber,
        string Title);

    private sealed record ProcessingWaitDetails(
        string? LibraryId,
        string? ReleaseName,
        string? SourcePath);

    private sealed record IntakeSyncPayload(
        string? SourceId,
        bool Manual);

    /// <summary>
    /// One lane. Lanes are separated by the resource they contend on, not by a
    /// generic "maintenance" grouping — metadata refreshes wait on a remote
    /// metadata provider, imports wait on disk, searches wait on indexers, and
    /// a catalogue recalculation waits on nothing but SQLite. Putting those in
    /// one lane made each of them wait behind the others for no reason.
    /// </summary>
    /// <param name="JobTypes">
    /// Empty means the lane only plans work and never executes it.
    /// </param>
    /// <param name="BatchSize">Jobs claimed per tick.</param>
    /// <param name="MaxConcurrency">Jobs from that batch run at once.</param>
    private sealed record JobLane(
        string Name,
        TimeSpan Interval,
        IReadOnlyList<string> JobTypes,
        bool PlanAutomation = false,
        bool PlanImports = false,
        bool PlanMaintenance = false,
        int BatchSize = 8,
        int MaxConcurrency = 4);
}
