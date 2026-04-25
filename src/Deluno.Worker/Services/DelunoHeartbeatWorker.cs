using Deluno.Jobs.Data;
using Deluno.Filesystem;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Metadata;
using Deluno.Integrations.Search;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Data;
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
            var platformSettingsRepository = scope.ServiceProvider.GetRequiredService<IPlatformSettingsRepository>();
            var mediaSearchPlanner = scope.ServiceProvider.GetRequiredService<IMediaSearchPlanner>();
            var downloadClientGrabService = scope.ServiceProvider.GetRequiredService<IDownloadClientGrabService>();
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
                        MaxItemsPerRun: library.MaxItemsPerRun))
                    .ToArray();

                await jobQueueRepository.PlanLibrarySearchesAsync(automationPlans, stoppingToken);
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
                    mediaSearchPlanner,
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

    private static async Task<string> ProcessJobAsync(
        Deluno.Jobs.Contracts.JobQueueItem job,
        IJobQueueRepository jobQueueRepository,
        IPlatformSettingsRepository platformSettingsRepository,
        IMediaSearchPlanner mediaSearchPlanner,
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
                    var candidates = await movieCatalogRepository.ListEligibleWantedAsync(
                        payload.LibraryId,
                        payload.MaxItems,
                        now,
                        ignoreRetryWindow,
                        cancellationToken);

                    foreach (var candidate in candidates)
                    {
                        var searchPlan = configuredSources == 0 || configuredClients == 0
                            ? new MediaSearchPlan(null, [], configuredSources == 0
                                ? "No indexers are linked to this library yet."
                                : "No download client is linked to this library yet.")
                            : await mediaSearchPlanner.BuildPlanAsync(
                                candidate.Title,
                                candidate.ReleaseYear,
                                "movies",
                                candidate.CurrentQuality,
                                candidate.TargetQuality,
                                routing!.Sources,
                                customFormats);

                        var outcome = configuredSources == 0 || configuredClients == 0
                            ? "blocked"
                            : searchPlan.BestCandidate is null
                                ? "checked"
                                : "matched";

                        if (outcome == "matched")
                        {
                            var downloadClient = routing!.DownloadClients
                                .OrderBy(item => item.Priority)
                                .First();
                            var grabResult = await GrabBestCandidateAsync(
                                downloadClientGrabService,
                                downloadClient.DownloadClientId,
                                searchPlan.BestCandidate!,
                                "movies",
                                "movies",
                                cancellationToken);

                            await jobQueueRepository.RecordDownloadDispatchAsync(
                                payload.LibraryId,
                                "movies",
                                "movie",
                                candidate.MovieId,
                                searchPlan.BestCandidate!.ReleaseName,
                                searchPlan.BestCandidate.IndexerName,
                                downloadClient.DownloadClientId,
                                downloadClient.DownloadClientName,
                                grabResult.Status,
                                SerializeSearchPlan(searchPlan, grabResult),
                                cancellationToken);
                        }

                        await movieCatalogRepository.RecordSearchAttemptAsync(
                            candidate.MovieId,
                            payload.LibraryId,
                            payload.TriggeredBy,
                            outcome,
                            now,
                            now.AddHours(Math.Max(1, payload.RetryDelayHours)),
                            BuildSearchResult(searchPlan, configuredClients),
                            searchPlan.BestCandidate?.ReleaseName,
                            searchPlan.BestCandidate?.IndexerName,
                            SerializeSearchPlan(searchPlan),
                            cancellationToken);
                    }

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

                foreach (var candidate in seriesCandidates)
                {
                    var searchPlan = configuredSources == 0 || configuredClients == 0
                        ? new MediaSearchPlan(null, [], configuredSources == 0
                            ? "No indexers are linked to this library yet."
                            : "No download client is linked to this library yet.")
                        : await mediaSearchPlanner.BuildPlanAsync(
                            candidate.Title,
                            candidate.StartYear,
                            "tv",
                            candidate.CurrentQuality,
                            candidate.TargetQuality,
                            routing!.Sources,
                            customFormats);

                    var outcome = configuredSources == 0 || configuredClients == 0
                        ? "blocked"
                        : searchPlan.BestCandidate is null
                            ? "checked"
                            : "matched";

                    if (outcome == "matched")
                    {
                        var downloadClient = routing!.DownloadClients
                            .OrderBy(item => item.Priority)
                            .First();
                        var grabResult = await GrabBestCandidateAsync(
                            downloadClientGrabService,
                            downloadClient.DownloadClientId,
                            searchPlan.BestCandidate!,
                            "tv",
                            "tv",
                            cancellationToken);

                        await jobQueueRepository.RecordDownloadDispatchAsync(
                            payload.LibraryId,
                            "tv",
                            "series",
                            candidate.SeriesId,
                            searchPlan.BestCandidate!.ReleaseName,
                            searchPlan.BestCandidate.IndexerName,
                            downloadClient.DownloadClientId,
                            downloadClient.DownloadClientName,
                            grabResult.Status,
                            SerializeSearchPlan(searchPlan, grabResult),
                            cancellationToken);
                    }

                    await seriesCatalogRepository.RecordSearchAttemptAsync(
                        candidate.SeriesId,
                        null,
                        payload.LibraryId,
                        payload.TriggeredBy,
                        outcome,
                        now,
                        now.AddHours(Math.Max(1, payload.RetryDelayHours)),
                        BuildSearchResult(searchPlan, configuredClients),
                        searchPlan.BestCandidate?.ReleaseName,
                        searchPlan.BestCandidate?.IndexerName,
                        SerializeSearchPlan(searchPlan),
                        cancellationToken);
                }

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

    private static string BuildSearchResult(MediaSearchPlan plan, int configuredClients)
    {
        if (plan.BestCandidate is null)
        {
            return plan.Summary;
        }

        return $"{plan.Summary} Ready to send to {configuredClients} download client{(configuredClients == 1 ? "" : "s")}.";
    }

    private static async Task<DownloadClientGrabResult> GrabBestCandidateAsync(
        IDownloadClientGrabService downloadClientGrabService,
        string downloadClientId,
        MediaSearchCandidate candidate,
        string mediaType,
        string category,
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
            new DownloadClientGrabRequest(
                candidate.ReleaseName,
                candidate.DownloadUrl,
                mediaType,
                category,
                candidate.IndexerName),
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

    private sealed record JobLane(
        string Name,
        TimeSpan Interval,
        IReadOnlyList<string> JobTypes,
        bool PlanAutomation);
}
