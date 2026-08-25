using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Quality.Data;
using Deluno.Series.Data;
using Deluno.Quality;

namespace Deluno.Worker.Jobs;

public sealed class EpisodeSearchJobHandler(
    ILibrariesRepository librariesRepository,
    IQualityRepository qualityRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    IJobQueueRepository jobQueueRepository,
    IAcquisitionDecisionPipeline acquisitionPipeline,
    IDownloadClientGrabService downloadClientGrabService,
    IActivityFeedRepository activityFeedRepository,
    TimeProvider timeProvider) : IJobHandler
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
        var allowedQualities = await QualityProfileResolver.ResolveAllowedQualitiesAsync(
            qualityRepository,
            library?.QualityProfileId,
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
                EpisodeNumber: payload.EpisodeNumber,
                AllowedQualities: allowedQualities),
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
