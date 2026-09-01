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
    IReleasePreferencePlanRepository? releasePreferencePlanRepository = null) : IJobHandler
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
        var routing = await librariesRepository.GetLibraryRoutingAsync(payload.LibraryId, cancellationToken);
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var library = libraries.FirstOrDefault(item => item.Id == payload.LibraryId);
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
        var allowedQualities = await QualityProfileResolver.ResolveAllowedQualitiesAsync(
            qualityRepository,
            library?.QualityProfileId,
            cancellationToken);
        var upgradeUntilCutoff = await QualityProfileResolver.ResolveUpgradeUntilCutoffAsync(
            qualityRepository,
            library?.QualityProfileId,
            cancellationToken);
        var preferencePlan = await QualityProfileResolver.ResolveReleasePreferencePlanAsync(
            qualityRepository,
            releasePreferencePlanRepository,
            library?.QualityProfileId,
            cancellationToken,
            customFormats);

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
                EpisodeNumber: payload.EpisodeNumber,
                AllowedQualities: allowedQualities,
                TagNames: mediaTagStore is null
                    ? []
                    : (await mediaTagStore.ListAsync(MediaKind.Series, payload.SeriesId, cancellationToken)).Select(tag => tag.Name).ToArray(),
                SearchKind: AcquisitionSearchKinds.Automatic,
                AvailableUtc: null,
                CurrentFilePresent: !string.IsNullOrWhiteSpace(currentFilePath),
                CurrentReleaseName: currentFilePath,
                UpgradeUntilCutoff: upgradeUntilCutoff,
                PreferencePlan: preferencePlan,
                NumberingScheme: numberingScheme,
                AbsoluteNumber: targetEpisode?.AbsoluteNumber,
                AirDate: targetEpisode?.AirDate,
                SceneSeasonNumber: targetEpisode?.SceneSeasonNumber,
                SceneEpisodeNumber: targetEpisode?.SceneEpisodeNumber,
                CurrentPreferenceEvaluation: currentPreferenceEvaluation),
            cancellationToken);

        var searchPlan = decisionPlan.SearchPlan;
        var bestCandidate = searchPlan.BestCandidate;
        var outcome = decisionPlan.Outcome;

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
            now.AddDays(1),
            decisionPlan.SearchResult,
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
