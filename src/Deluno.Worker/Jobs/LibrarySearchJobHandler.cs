using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Quality.Data;
using Deluno.Series.Data;

namespace Deluno.Worker.Jobs;

public sealed class LibrarySearchJobHandler(
    ILibrariesRepository librariesRepository,
    IQualityRepository qualityRepository,
    IJobQueueRepository jobQueueRepository,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    IAcquisitionDecisionPipeline acquisitionPipeline,
    IDownloadClientGrabService downloadClientGrabService,
    IActivityFeedRepository activityFeedRepository,
    TimeProvider timeProvider) : IJobHandler
{
    public string JobType => "library.search";

    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        var payload = JobPayloads.ParseLibraryPayload(job.PayloadJson);
        if (payload is null || string.IsNullOrWhiteSpace(payload.LibraryName))
        {
            return "Finished checking a library.";
        }

        var now = timeProvider.GetUtcNow();
        var routing = await librariesRepository.GetLibraryRoutingAsync(payload.LibraryId, cancellationToken);
        var configuredSources = routing?.Sources.Count ?? 0;
        var configuredClients = routing?.DownloadClients.Count ?? 0;
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var library = libraries.FirstOrDefault(item => item.Id == payload.LibraryId);
        var searchStatus = ResolveSearchStatus(payload);
        var customFormats = await SearchExecutionSupport.ResolveCustomFormatsAsync(
            qualityRepository,
            library?.QualityProfileId,
            cancellationToken);

        if (payload.MediaType == "movies")
        {
            return await SearchMoviesAsync(job, payload, searchStatus, routing, customFormats, configuredSources, configuredClients, now, cancellationToken);
        }

        return await SearchSeriesAsync(job, payload, searchStatus, routing, customFormats, configuredSources, configuredClients, now, cancellationToken);
    }

    private async Task<string> SearchMoviesAsync(
        JobQueueItem job,
        JobPayloads.LibrarySearchPayload payload,
        string? searchStatus,
        LibraryRoutingSnapshot? routing,
        IReadOnlyList<Deluno.Quality.Contracts.CustomFormatItem> customFormats,
        int configuredSources,
        int configuredClients,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var ignoreRetryWindow = string.Equals(payload.TriggeredBy, "manual", StringComparison.OrdinalIgnoreCase);
        var startedUtc = now;
        var retryDelayed = ignoreRetryWindow
            ? 0
            : await movieCatalogRepository.CountRetryDelayedWantedAsync(payload.LibraryId, now, cancellationToken, searchStatus);
        var candidates = (await movieCatalogRepository.ListEligibleWantedAsync(
            payload.LibraryId,
            payload.MaxItems,
            now,
            ignoreRetryWindow,
            cancellationToken,
            searchStatus))
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
                    SearchExecutionSupport.NormalizeActionKind(candidate.WantedStatus),
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
                var grabResult = await SearchExecutionSupport.GrabBestCandidateAsync(
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
                    SearchExecutionSupport.SerializeSearchPlan(searchPlan, grabResult),
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
                SearchExecutionSupport.SerializeSearchPlan(searchPlan),
                cancellationToken);

            var nextEligibleUtc = now.AddHours(Math.Max(1, payload.RetryDelayHours));
            await jobQueueRepository.RecordSearchRetryWindowAsync(
                "movie",
                candidate.MovieId,
                payload.LibraryId,
                "movies",
                SearchExecutionSupport.NormalizeActionKind(candidate.WantedStatus),
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
                SearchExecutionSupport.SerializeCycleNotes(configuredSources, configuredClients, checkedCount, matchedCount, blockedCount, heldCount, retryDelayed, payload.MaxItems, apiCallCount, queuedReleaseBytes),
                startedUtc,
                timeProvider.GetUtcNow(),
                searchStatus ?? "combined"),
            cancellationToken);

        await activityFeedRepository.RecordActivityAsync(
            "library.search.executed",
            SearchExecutionSupport.FormatExecutionMessage(payload.LibraryName, candidates.Length, configuredSources, configuredClients, "movie"),
            null,
            job.Id,
            "library",
            payload.LibraryId,
            cancellationToken);

        return SearchExecutionSupport.FormatCompletionMessage(payload.LibraryName, candidates.Length, configuredSources, configuredClients, "movie");
    }

    private async Task<string> SearchSeriesAsync(
        JobQueueItem job,
        JobPayloads.LibrarySearchPayload payload,
        string? searchStatus,
        LibraryRoutingSnapshot? routing,
        IReadOnlyList<Deluno.Quality.Contracts.CustomFormatItem> customFormats,
        int configuredSources,
        int configuredClients,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var seriesIgnoreRetryWindow = string.Equals(payload.TriggeredBy, "manual", StringComparison.OrdinalIgnoreCase);
        var seriesCandidates = (await seriesCatalogRepository.ListEligibleWantedAsync(
            payload.LibraryId,
            payload.MaxItems,
            now,
            seriesIgnoreRetryWindow,
            cancellationToken,
            searchStatus))
            .Where(candidate => string.IsNullOrWhiteSpace(payload.TargetEntityId) || string.Equals(candidate.SeriesId, payload.TargetEntityId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var seriesStartedUtc = now;
        var seriesRetryDelayed = seriesIgnoreRetryWindow
            ? 0
            : await seriesCatalogRepository.CountRetryDelayedWantedAsync(payload.LibraryId, now, cancellationToken, searchStatus);
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
                    SearchExecutionSupport.NormalizeActionKind(candidate.WantedStatus),
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
                var grabResult = await SearchExecutionSupport.GrabBestCandidateAsync(
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
                    SearchExecutionSupport.SerializeSearchPlan(searchPlan, grabResult),
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
                SearchExecutionSupport.SerializeSearchPlan(searchPlan),
                cancellationToken);

            var nextEligibleUtc = now.AddHours(Math.Max(1, payload.RetryDelayHours));
            await jobQueueRepository.RecordSearchRetryWindowAsync(
                "series",
                candidate.SeriesId,
                payload.LibraryId,
                "tv",
                SearchExecutionSupport.NormalizeActionKind(candidate.WantedStatus),
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
                SearchExecutionSupport.SerializeCycleNotes(configuredSources, configuredClients, seriesCheckedCount, seriesMatchedCount, seriesBlockedCount, seriesHeldCount, seriesRetryDelayed, payload.MaxItems, seriesApiCallCount, seriesQueuedReleaseBytes),
                seriesStartedUtc,
                timeProvider.GetUtcNow(),
                searchStatus ?? "combined"),
            cancellationToken);

        await activityFeedRepository.RecordActivityAsync(
            "library.search.executed",
            SearchExecutionSupport.FormatExecutionMessage(payload.LibraryName, seriesCandidates.Length, configuredSources, configuredClients, "TV show"),
            null,
            job.Id,
            "library",
            payload.LibraryId,
            cancellationToken);

        return SearchExecutionSupport.FormatCompletionMessage(payload.LibraryName, seriesCandidates.Length, configuredSources, configuredClients, "TV show");
    }

    private static string? ResolveSearchStatus(JobPayloads.LibrarySearchPayload payload)
    {
        if (string.Equals(payload.SearchKind, "missing", StringComparison.OrdinalIgnoreCase)) return "missing";
        if (string.Equals(payload.SearchKind, "upgrade", StringComparison.OrdinalIgnoreCase)) return "upgrade";
        if (payload.CheckMissing && !payload.CheckUpgrades) return "missing";
        if (payload.CheckUpgrades && !payload.CheckMissing) return "upgrade";
        return null;
    }
}
