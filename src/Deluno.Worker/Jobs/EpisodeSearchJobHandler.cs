using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Quality.Data;
using Deluno.Series.Data;
using Deluno.Series.Contracts;
using Deluno.Quality;
using Deluno.Media;
using Deluno.Quality.ReleasePreferences;
using Deluno.Platform.Contracts;

namespace Deluno.Worker.Jobs;

public sealed class EpisodeSearchJobHandler(
    ILibrariesRepository librariesRepository,
    IQualityRepository qualityRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    IJobQueueRepository jobQueueRepository,
    IAcquisitionDecisionPipeline acquisitionPipeline,
    IDownloadClientGrabService downloadClientGrabService,
    IActivityFeedRepository activityFeedRepository,
    TimeProvider timeProvider,
    IMediaTagStore? mediaTagStore = null,
    IMediaStateRepository? mediaStateRepository = null,
    IReleasePreferencePlanRepository? releasePreferencePlanRepository = null,
    IBlockedReleaseRepository? blockedReleases = null,
    IJobScheduler? jobScheduler = null) : IJobHandler
{
    public string JobType => "episode.search";

    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        var payload = JobPayloads.ParseEpisodeSearchPayload(job.PayloadJson);
        if (payload is null || string.IsNullOrWhiteSpace(payload.EpisodeId))
        {
            return "Finished searching for episode.";
        }

        var now = timeProvider.GetUtcNow();
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var library = libraries.FirstOrDefault(item => item.Id == payload.LibraryId);
        // The library's own Retry after, as film and series searches use. This
        // was a fixed day, whatever the library said (#592).
        var nextEligibleUtc = now.AddHours(Math.Max(1, library?.RetryDelayHours ?? 24));

        // Episode searches are queued by the TV library's own cycle, so this is
        // the routine search #583 holds once the processor has refused enough
        // copies of the episode. Checked before any indexer is asked.
        if (blockedReleases is not null)
        {
            var refused = ProcessorRefusals.Count(await blockedReleases.ListForAsync("tv", payload.EpisodeId, cancellationToken));
            if (ProcessorRefusals.StopSearching(refused))
            {
                await seriesCatalogRepository.RecordSearchAttemptAsync(
                    payload.SeriesId,
                    payload.EpisodeId,
                    payload.LibraryId,
                    "automatic",
                    "held",
                    now,
                    nextEligibleUtc,
                    ProcessorRefusals.WhySearchIsHeld(refused),
                    null,
                    null,
                    null,
                    cancellationToken);
                return $"Held the search for episode S{payload.SeasonNumber:D2}E{payload.EpisodeNumber:D2}: the processor has refused {refused} copies.";
            }
        }

        var routing = await librariesRepository.GetLibraryRoutingAsync(payload.LibraryId, cancellationToken);
        var customFormats = await SearchExecutionSupport.ResolveCustomFormatsAsync(
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
        var currentFilePath = await seriesCatalogRepository.GetEpisodeFilePathAsync(
            payload.EpisodeId,
            cancellationToken);
        var currentFileSizeBytes = await seriesCatalogRepository.GetEpisodeFileSizeBytesAsync(
            payload.EpisodeId,
            cancellationToken);
        // The scheduled payload intentionally contains only the canonical
        // episode identity. Resolve the series numbering at execution time so
        // old queued jobs and jobs created before a numbering edit still use
        // the current daily/anime/absolute/scene query keys.
        var inventory = await seriesCatalogRepository.GetInventoryDetailAsync(
            payload.SeriesId,
            cancellationToken);
        // The show's own runtime, not the episode's: TMDB (and this catalogue)
        // carries episode runtime at series level, not per episode (#630, #638).
        var seriesItem = await seriesCatalogRepository.GetByIdAsync(payload.SeriesId, cancellationToken);
        var targetEpisode = inventory?.Episodes.FirstOrDefault(
            episode => string.Equals(episode.EpisodeId, payload.EpisodeId, StringComparison.OrdinalIgnoreCase));
        var numberingScheme = inventory?.Numbering?.NumberingScheme;
        // An episode must not inherit a series-level snapshot for a different
        // file. Query by the exact path; if no episode snapshot exists the
        // decision engine holds automatic replacement until the independent
        // file probe supplies a same-plan baseline. A path is not proof of the
        // container's codec or audio tracks.
        var currentPreferenceEvaluation = mediaStateRepository is null || string.IsNullOrWhiteSpace(currentFilePath)
            ? null
            : await mediaStateRepository.GetLatestPreferenceEvaluationSnapshotAsync(
                MediaKind.Series,
                payload.SeriesId,
                payload.LibraryId,
                 fileIdentity: null,
                 cancellationToken,
                 filePath: currentFilePath,
                 fileSizeBytes: currentFileSizeBytes);
        var profileRules = await QualityProfileResolver.ResolveSearchRulesAsync(
            qualityRepository,
            library?.QualityProfileId,
            cancellationToken);
        var preferencePlan = await QualityProfileResolver.ResolveReleasePreferencePlanAsync(
            qualityRepository,
            releasePreferencePlanRepository,
            library?.QualityProfileId,
            cancellationToken,
            customFormats);

        var episodeTitleTags = mediaTagStore is null
            ? []
            : (await mediaTagStore.ListAsync(MediaKind.Series, payload.SeriesId, cancellationToken)).Select(tag => tag.Name).ToArray();
        var decisionPlan = await acquisitionPipeline.PlanAsync(
            new AcquisitionDecisionRequest(
                // The show, not the episode: an indexer reads a TV search's
                // query as the series. The payload's title is the episode's,
                // kept for Activity, and searching by it found nothing (#590).
                Title: inventory?.Title ?? payload.Title,
                Year: null,
                MediaType: "tv",
                CurrentQuality: currentQuality,
                TargetQuality: targetQuality,
                Sources: routing?.Sources ?? [],
                DownloadClients: routing?.DownloadClients ?? [],
                CustomFormats: customFormats,
                SeasonNumber: payload.SeasonNumber,
                EpisodeNumber: payload.EpisodeNumber,
                TagNames: episodeTitleTags,
                SearchKind: AcquisitionSearchKinds.Automatic,
                AvailableUtc: null,
                CurrentFilePresent: !string.IsNullOrWhiteSpace(currentFilePath),
                CurrentReleaseName: currentFilePath,
                PreferencePlan: preferencePlan,
                NumberingScheme: numberingScheme,
                AbsoluteNumber: targetEpisode?.AbsoluteNumber,
                AirDate: targetEpisode?.AirDate,
                SceneSeasonNumber: targetEpisode?.SceneSeasonNumber,
                SceneEpisodeNumber: targetEpisode?.SceneEpisodeNumber,
                CurrentPreferenceEvaluation: currentPreferenceEvaluation,
                RuntimeMinutes: seriesItem?.RuntimeMinutes,
                ImdbId: seriesItem?.ImdbId,
                Genres: seriesItem?.Genres,
                OtherTitles: CatalogueMetadata.ReadAlternateTitles(seriesItem?.MetadataJson)).WithProfile(profileRules),
            cancellationToken);

        var searchPlan = decisionPlan.SearchPlan;
        SearchExecutionSupport.DeferIfIndexersWereBusy(searchPlan);
        var bestCandidate = searchPlan.BestCandidate;
        var outcome = decisionPlan.Outcome;
        var searchResult = decisionPlan.SearchResult;

        // An episode has no downloading mark, so this is what stops it being
        // grabbed again while its download is on its way. Its series counts too:
        // a series-level grab can be this very episode, or a pack holding it.
        var onItsWay = decisionPlan.ShouldDispatch && bestCandidate is not null
            ? await SearchExecutionSupport.AlreadyOnItsWayAsync(jobQueueRepository, bestCandidate.ReleaseName, [payload.EpisodeId, payload.SeriesId], cancellationToken)
            : null;
        if (onItsWay is not null)
        {
            outcome = "skipped";
            searchResult = onItsWay;
        }

        if (onItsWay is null && decisionPlan.ShouldDispatch && decisionPlan.SelectedDownloadClient is not null && decisionPlan.DispatchRequest is not null)
        {
            var downloadClient = decisionPlan.SelectedDownloadClient;
            var grabResult = await SearchExecutionSupport.GrabBestCandidateAsync(
                downloadClientGrabService,
                downloadClient.DownloadClientId,
                bestCandidate!,
                decisionPlan.DispatchRequest,
                cancellationToken);

            await SearchExecutionSupport.BlockDeadReleaseAsync(
                blockedReleases,
                activityFeedRepository,
                grabResult,
                "tv",
                "episode",
                payload.EpisodeId,
                seriesItem?.Title ?? bestCandidate!.ReleaseName,
                bestCandidate!.IndexerName,
                downloadClient.DownloadClientName,
                cancellationToken);

            await SearchExecutionSupport.RecordDuplicateFollowAsync(
                activityFeedRepository,
                grabResult,
                "episode",
                payload.EpisodeId,
                cancellationToken);

            await SearchExecutionSupport.RetryConnectivityFailureAsync(
                jobScheduler,
                activityFeedRepository,
                grabResult,
                decisionPlan.DispatchRequest,
                timeProvider,
                payload.LibraryId,
                "tv",
                "episode",
                payload.EpisodeId,
                seriesItem?.Title ?? bestCandidate!.ReleaseName,
                downloadClient.DownloadClientId,
                downloadClient.DownloadClientName,
                cancellationToken);

            await SearchExecutionSupport.RecordAutomaticGrabRoutingAsync(
                activityFeedRepository,
                acquisitionPipeline,
                grabResult,
                downloadClient.DownloadClientId,
                downloadClient.DownloadClientName,
                episodeTitleTags,
                "episode",
                payload.EpisodeId,
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
                SearchExecutionSupport.SerializeSearchPlan(searchPlan, grabResult),
                grabResponseCode: grabResult.Succeeded ? 200 : 400,
                grabFailureCode: grabResult.Failure?.Code ?? grabResult.FailureCode,
                cancellationToken: cancellationToken,
                failure: grabResult.Failure,
                replacementAuthorized: !string.IsNullOrWhiteSpace(currentFilePath),
                replacementExpectedPath: currentFilePath,
                clientExternalId: grabResult.ExternalId);
        }

        await seriesCatalogRepository.RecordSearchAttemptAsync(
            payload.SeriesId,
            payload.EpisodeId,
            payload.LibraryId,
            "automatic",
            outcome,
            now,
            nextEligibleUtc,
            searchResult,
            bestCandidate?.ReleaseName,
            bestCandidate?.IndexerName,
            SearchExecutionSupport.SerializeSearchPlan(searchPlan),
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
}
