using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Quality.Data;
using Deluno.Series.Data;
using Deluno.Quality;

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
        var allowedQualities = await QualityProfileResolver.ResolveAllowedQualitiesAsync(
            qualityRepository,
            library?.QualityProfileId,
            cancellationToken);

        if (payload.MediaType == "movies")
        {
            return await SearchMoviesAsync(job, payload, searchStatus, routing, customFormats, allowedQualities, configuredSources, configuredClients, now, cancellationToken);
        }

        return await SearchSeriesAsync(job, payload, searchStatus, routing, customFormats, allowedQualities, configuredSources, configuredClients, now, cancellationToken);
    }

    private async Task<string> SearchMoviesAsync(
        JobQueueItem job,
        JobPayloads.LibrarySearchPayload payload,
        string? searchStatus,
        LibraryRoutingSnapshot? routing,
        IReadOnlyList<Deluno.Quality.Contracts.CustomFormatItem> customFormats,
        IReadOnlyList<string> allowedQualities,
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
                    customFormats,
                    AllowedQualities: allowedQualities),
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
        IReadOnlyList<string> allowedQualities,
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
                    customFormats,
                    AllowedQualities: allowedQualities),
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

        // The episodes the series pass cannot reach (#303).
        //
        // `library.search` searches at the series level — a season pack, a
        // series-level release. Individual episodes have their own job type,
        // `episode.search`, their own handler and their own planner, and until
        // now nothing called the planner: three pieces that each worked and
        // never met. A show missing four scattered episodes was searched for as
        // a show, found nothing at that level, and the four were never asked
        // for.
        //
        // Planned here rather than in the heartbeat's automation lane, which is
        // where the missing call was expected. The lane would have needed its
        // own copy of every gate this cycle already passed — the time-of-day
        // window, the search interval, missing-versus-upgrade, the manual
        // override and `MaxItemsPerRun` — and a second copy of a scheduling
        // rule is how the last four defects in this codebase were built. Riding
        // on the cycle, the episode pass is due exactly when the series pass is
        // due, and asks for the same half of the work.
        var eligibleEpisodes = (await seriesCatalogRepository.ListEligibleWantedEpisodesAsync(
            payload.LibraryId,
            payload.MaxItems,
            now,
            seriesIgnoreRetryWindow,
            cancellationToken,
            searchStatus))
            .Where(episode => string.IsNullOrWhiteSpace(payload.TargetEntityId) || string.Equals(episode.SeriesId, payload.TargetEntityId, StringComparison.OrdinalIgnoreCase))
            .Select(episode => new EpisodeSearchPlanItem(
                EpisodeId: episode.EpisodeId,
                SeriesId: episode.SeriesId,
                SeasonNumber: episode.SeasonNumber,
                EpisodeNumber: episode.EpisodeNumber,
                // An episode with no title yet is still worth searching for; the
                // plan payload names it by its code, which is what an indexer
                // query uses anyway.
                Title: episode.Title ?? $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}"))
            .ToArray();

        // The planner de-duplicates against jobs already queued for the same
        // episode, so a cycle that runs while the last one is still working
        // through its episodes adds nothing rather than doubling it.
        await jobQueueRepository.PlanEpisodeSearchesAsync(payload.LibraryId, eligibleEpisodes, cancellationToken);

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
