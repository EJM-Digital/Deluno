using Deluno.Jobs.Data;
using Deluno.Filesystem;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Metadata;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Movies.Data;
using Deluno.Movies.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Series.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

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
    private DateTimeOffset _lastImportAutomationUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDispatchCleanupUtc = DateTimeOffset.MinValue;
    private readonly JobLane[] _lanes =
    [
        new("search", TimeSpan.FromSeconds(5), ["library.search"], PlanAutomation: true),
        new("import", TimeSpan.FromSeconds(2), ["filesystem.import.execute"], PlanAutomation: false),
        new("maintenance", TimeSpan.FromSeconds(8),
        [
            "movies.metadata.refresh",
            "series.metadata.refresh",
            "movies.quality.recalculate",
            "series.quality.recalculate",
            "movies.catalog.refresh",
            "series.catalog.refresh"
        ], PlanAutomation: false)
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Deluno worker runtime started as {WorkerId}.", _workerId);

        await Task.WhenAll(_lanes.Select(lane => RunLaneAsync(lane, stoppingToken)));
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
            var jobQueueRepository = scope.ServiceProvider.GetRequiredService<IJobQueueRepository>();
            var jobScheduler = scope.ServiceProvider.GetRequiredService<IJobScheduler>();
            var platformSettingsRepository = scope.ServiceProvider.GetRequiredService<IPlatformSettingsRepository>();
            var acquisitionPipeline = scope.ServiceProvider.GetRequiredService<IAcquisitionDecisionPipeline>();
            var downloadClientGrabService = scope.ServiceProvider.GetRequiredService<IDownloadClientGrabService>();
            var downloadClientTelemetryService = scope.ServiceProvider.GetRequiredService<IDownloadClientTelemetryService>();
            var metadataProvider = scope.ServiceProvider.GetRequiredService<IMetadataProvider>();
            var importPipelineService = scope.ServiceProvider.GetRequiredService<IImportPipelineService>();
            var movieCatalogRepository = scope.ServiceProvider.GetRequiredService<IMovieCatalogRepository>();
            var seriesCatalogRepository = scope.ServiceProvider.GetRequiredService<ISeriesCatalogRepository>();
            var activityFeedRepository = scope.ServiceProvider.GetRequiredService<IActivityFeedRepository>();

            await jobQueueRepository.HeartbeatAsync(_workerId, stoppingToken);

            var settings = await platformSettingsRepository.GetAsync(stoppingToken);
            if (!settings.AutoStartJobs)
            {
                logger.LogDebug("Worker {WorkerId} lane {LaneName} tick with auto-start disabled.", _workerId, lane.Name);
                continue;
            }

            if (lane.PlanAutomation)
            {
                var libraries = await platformSettingsRepository.ListLibrariesAsync(stoppingToken);
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
            }

            if (lane.Name == "import")
            {
                await PlanImportAutomationAsync(
                    jobScheduler,
                    jobQueueRepository,
                    platformSettingsRepository,
                    downloadClientTelemetryService,
                    activityFeedRepository,
                    movieCatalogRepository,
                    seriesCatalogRepository,
                    timeProvider,
                    stoppingToken);
            }

            if (lane.Name == "maintenance")
            {
                var cleanupService = scope.ServiceProvider.GetRequiredService<IDispatchCleanupService>();
                await RunDispatchCleanupAsync(cleanupService, timeProvider, stoppingToken);
            }

            var job = await jobQueueRepository.LeaseNextAsync(
                $"{_workerId}-{lane.Name}",
                TimeSpan.FromMinutes(2),
                lane.JobTypes,
                stoppingToken);

            if (job is null)
            {
                logger.LogDebug("Worker {WorkerId} lane {LaneName} tick with no pending jobs.", _workerId, lane.Name);
                continue;
            }

            try
            {
                logger.LogInformation("Processing job {JobId} of type {JobType} on lane {LaneName}.", job.Id, job.JobType, lane.Name);
                var message = await ProcessJobAsync(
                    job,
                    jobQueueRepository,
                    platformSettingsRepository,
                    acquisitionPipeline,
                    downloadClientGrabService,
                    metadataProvider,
                    importPipelineService,
                    movieCatalogRepository,
                    seriesCatalogRepository,
                    activityFeedRepository,
                    timeProvider,
                    stoppingToken);

                await jobQueueRepository.CompleteAsync(job.Id, $"{_workerId}-{lane.Name}", message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker {WorkerId} lane {LaneName} failed processing job {JobId}.", _workerId, lane.Name, job.Id);
                await jobQueueRepository.FailAsync(job.Id, $"{_workerId}-{lane.Name}", ex.Message, stoppingToken);
            }
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

    private async Task PlanImportAutomationAsync(
        IJobScheduler jobScheduler,
        IJobQueueRepository jobQueueRepository,
        IPlatformSettingsRepository platformSettingsRepository,
        IDownloadClientTelemetryService downloadClientTelemetryService,
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

        var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
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
        await PlanProcessorOutputImportsAsync(
            jobScheduler,
            activityFeedRepository,
            libraries,
            knownImportSources,
            cancellationToken);
        await RecordProcessorTimeoutsAsync(
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
                var waitKey = $"{item.ClientId}:{item.Id}:{sourceKey}";
                if (recentWaitingKeys.Add(waitKey))
                {
                    await activityFeedRepository.RecordActivityAsync(
                        "processing.waiting",
                        $"{item.Title} is complete in {item.ClientName}; Deluno is waiting for {library.ProcessorName ?? "the configured processor"} to produce a cleaned output.",
                        JsonSerializer.Serialize(new
                        {
                            item.ClientId,
                            item.ClientName,
                            item.ReleaseName,
                            item.SourcePath,
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
        IAcquisitionDecisionPipeline acquisitionPipeline,
        IDownloadClientGrabService downloadClientGrabService,
        IMetadataProvider metadataProvider,
        IImportPipelineService importPipelineService,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        IActivityFeedRepository activityFeedRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (job.JobType == "library.search")
        {
            var payload = ParseLibraryPayload(job.PayloadJson);
            if (payload is not null && !string.IsNullOrWhiteSpace(payload.LibraryName))
            {
                var now = timeProvider.GetUtcNow();
                var routing = await platformSettingsRepository.GetLibraryRoutingAsync(payload.LibraryId, cancellationToken);
                var configuredSources = routing?.Sources.Count ?? 0;
                var configuredClients = routing?.DownloadClients.Count ?? 0;
                var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
                var library = libraries.FirstOrDefault(item => item.Id == payload.LibraryId);
                var customFormats = await ResolveCustomFormatsAsync(
                    platformSettingsRepository,
                    library?.QualityProfileId,
                    cancellationToken);

                if (payload.MediaType == "movies")
                {
                    var ignoreRetryWindow = string.Equals(payload.TriggeredBy, "manual", StringComparison.OrdinalIgnoreCase);
                    var startedUtc = now;
                    var retryDelayed = ignoreRetryWindow
                        ? 0
                        : await movieCatalogRepository.CountRetryDelayedWantedAsync(payload.LibraryId, now, cancellationToken);
                    var candidates = await movieCatalogRepository.ListEligibleWantedAsync(
                        payload.LibraryId,
                        payload.MaxItems,
                        now,
                        ignoreRetryWindow,
                        cancellationToken);
                    var matchedCount = 0;
                    var blockedCount = 0;
                    var checkedCount = 0;
                    var heldCount = 0;

                    foreach (var candidate in candidates)
                    {
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
                            candidates.Count > 0 || retryDelayed > 0 ? "completed" : "empty",
                            candidates.Count,
                            matchedCount,
                            retryDelayed,
                            SerializeCycleNotes(configuredSources, configuredClients, checkedCount, matchedCount, blockedCount, heldCount, retryDelayed, payload.MaxItems),
                            startedUtc,
                            timeProvider.GetUtcNow()),
                        cancellationToken);

                    await activityFeedRepository.RecordActivityAsync(
                        "library.search.executed",
                        FormatExecutionMessage(payload.LibraryName, candidates.Count, configuredSources, configuredClients, "movie"),
                        null,
                        job.Id,
                        "library",
                        payload.LibraryId,
                        cancellationToken);

                    return FormatCompletionMessage(payload.LibraryName, candidates.Count, configuredSources, configuredClients, "movie");
                }

                var seriesCandidates = await seriesCatalogRepository.ListEligibleWantedAsync(
                    payload.LibraryId,
                    payload.MaxItems,
                    now,
                    string.Equals(payload.TriggeredBy, "manual", StringComparison.OrdinalIgnoreCase),
                    cancellationToken);
                var seriesIgnoreRetryWindow = string.Equals(payload.TriggeredBy, "manual", StringComparison.OrdinalIgnoreCase);
                var seriesStartedUtc = now;
                var seriesRetryDelayed = seriesIgnoreRetryWindow
                    ? 0
                    : await seriesCatalogRepository.CountRetryDelayedWantedAsync(payload.LibraryId, now, cancellationToken);
                var seriesMatchedCount = 0;
                var seriesBlockedCount = 0;
                var seriesCheckedCount = 0;
                var seriesHeldCount = 0;

                foreach (var candidate in seriesCandidates)
                {
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
                        seriesCandidates.Count > 0 || seriesRetryDelayed > 0 ? "completed" : "empty",
                        seriesCandidates.Count,
                        seriesMatchedCount,
                        seriesRetryDelayed,
                        SerializeCycleNotes(configuredSources, configuredClients, seriesCheckedCount, seriesMatchedCount, seriesBlockedCount, seriesHeldCount, seriesRetryDelayed, payload.MaxItems),
                        seriesStartedUtc,
                        timeProvider.GetUtcNow()),
                    cancellationToken);

                await activityFeedRepository.RecordActivityAsync(
                    "library.search.executed",
                    FormatExecutionMessage(payload.LibraryName, seriesCandidates.Count, configuredSources, configuredClients, "TV show"),
                    null,
                    job.Id,
                    "library",
                    payload.LibraryId,
                    cancellationToken);

                return FormatCompletionMessage(payload.LibraryName, seriesCandidates.Count, configuredSources, configuredClients, "TV show");
            }

            return "Finished checking a library.";
        }

        if (job.JobType == "episode.search")
        {
            var payload = ParseEpisodeSearchPayload(job.PayloadJson);
            if (payload is not null && !string.IsNullOrWhiteSpace(payload.EpisodeId))
            {
                var now = timeProvider.GetUtcNow();
                var routing = await platformSettingsRepository.GetLibraryRoutingAsync(payload.LibraryId, cancellationToken);
                var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
                var library = libraries.FirstOrDefault(item => item.Id == payload.LibraryId);
                var customFormats = await ResolveCustomFormatsAsync(
                    platformSettingsRepository,
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

    private static async Task PlanProcessorOutputImportsAsync(
        IJobScheduler jobScheduler,
        IActivityFeedRepository activityFeedRepository,
        IReadOnlyList<LibraryItem> libraries,
        ISet<string> knownImportSources,
        CancellationToken cancellationToken)
    {
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

                var title = Path.GetFileNameWithoutExtension(file);
                var request = new ImportExecuteRequest(
                    Preview: new ImportPreviewRequest(
                        SourcePath: file,
                        FileName: Path.GetFileName(file),
                        MediaType: library.MediaType,
                        Title: title,
                        Year: InferYear(title),
                        Genres: [],
                        Tags: ["processed"],
                        Studio: null,
                        OriginalLanguage: null),
                    TransferMode: "auto",
                    Overwrite: false,
                    AllowCopyFallback: true,
                    ForceReplacement: false);

                var job = await jobScheduler.EnqueueAsync(
                    new EnqueueJobRequest(
                        JobType: "filesystem.import.execute",
                        Source: "processor-output-watcher",
                        PayloadJson: JsonSerializer.Serialize(request, PayloadJsonOptions),
                        RelatedEntityType: library.MediaType == "tv" ? "series" : "movie",
                        RelatedEntityId: null),
                    cancellationToken);

                knownImportSources.Add(sourceKey);
                await activityFeedRepository.RecordActivityAsync(
                    "processing.output.import-queued",
                    $"{library.ProcessorName ?? "Processor"} output was detected and queued for import into {library.Name}.",
                    JsonSerializer.Serialize(new
                    {
                        LibraryId = library.Id,
                        LibraryName = library.Name,
                        library.MediaType,
                        SourcePath = file,
                        JobId = job.Id
                    }, PayloadJsonOptions),
                    job.Id,
                    "library",
                    library.Id,
                    cancellationToken);
            }
        }
    }

    private static async Task RecordProcessorTimeoutsAsync(
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

        await activityFeedRepository.RecordActivityAsync(
            "metadata.series.refreshed",
            $"{series.Title} metadata was refreshed by the background worker.",
            JsonSerializer.Serialize(match, PayloadJsonOptions),
            job.Id,
            "series",
            series.Id,
            stoppingToken);

        return $"Refreshed metadata for {series.Title}.";
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
        int maxItems)
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
            maxItems
        }, PayloadJsonOptions);
    }

    private static string NormalizeActionKind(string? wantedStatus)
        => string.Equals(wantedStatus, "upgrade", StringComparison.OrdinalIgnoreCase)
            ? "upgrade"
            : "missing";

    private static async Task<IReadOnlyList<CustomFormatItem>> ResolveCustomFormatsAsync(
        IPlatformSettingsRepository repository,
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

    private static LibraryItem? ResolveLibraryForQueueItem(DownloadQueueItem item, IReadOnlyList<LibraryItem> libraries)
    {
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
        string TriggeredBy);

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

    private sealed record JobLane(
        string Name,
        TimeSpan Interval,
        IReadOnlyList<string> JobTypes,
        bool PlanAutomation);
}
