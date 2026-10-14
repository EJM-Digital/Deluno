using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Movies.Data;
using Deluno.Platform.Data;
using Deluno.Quality.Data;
using Deluno.Series.Data;

namespace Deluno.Worker.Jobs;

/// <summary>
/// Searches one chosen film or show, right now, because a bulk search or the bulk editor asked for it. It runs the
/// same search as "Search now" on the title, in the library the title is wanted in, and writes to Activity what
/// started it. It never flags a library, so the rest of the library is not searched (#1313).
/// </summary>
public abstract class TitleSearchJobHandler(
    MediaKind kind,
    IMediaStateRepository mediaStateRepository,
    ILibrariesRepository librariesRepository,
    IQualityRepository qualityRepository,
    IJobQueueRepository jobQueueRepository,
    IAcquisitionDecisionPipeline acquisitionPipeline,
    IDownloadClientGrabService downloadClientGrabService,
    IActivityFeedRepository activityFeedRepository,
    TimeProvider timeProvider,
    RecordMediaSearchAttemptAsync recordSearchAttempt,
    Func<string, CancellationToken, Task<IReadOnlyDictionary<int, IReadOnlyDictionary<int, string>>?>>? episodeTitles,
    IMediaTagStore? mediaTagStore,
    IReleasePreferencePlanRepository? releasePreferencePlanRepository,
    IPlatformSettingsRepository? platformSettingsRepository) : IJobHandler
{
    public abstract string JobType { get; }

    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        var (id, libraryId, startedBy) = Read(job.PayloadJson, job.RelatedEntityId);
        if (string.IsNullOrWhiteSpace(id))
        {
            return "This search does not say which title it is for, so nothing was searched.";
        }

        var entityType = kind == MediaKind.Movie ? "movie" : "series";
        var result = await MediaSearchHandler.ExecuteAsync(
            kind,
            id,
            mode: null,
            mediaStateRepository,
            librariesRepository,
            qualityRepository,
            jobQueueRepository,
            acquisitionPipeline,
            downloadClientGrabService,
            activityFeedRepository,
            timeProvider,
            recordSearchAttempt,
            cancellationToken,
            mediaTagStore,
            releasePreferencePlanRepository,
            libraryId,
            platformSettingsRepository,
            episodeTitles is null ? null : token => episodeTitles(id, token));

        if (result.NotFound)
        {
            return "That title is no longer in the library, so nothing was searched.";
        }

        var title = (await mediaStateRepository.GetByIdAsync(kind, id, cancellationToken))?.Title ?? id;
        await activityFeedRepository.RecordActivityAsync(
            $"{entityType}.search.bulk",
            $"Searched for {title}, started from {startedBy}. {result.Summary}".Trim(),
            null,
            job.Id,
            entityType,
            id,
            cancellationToken);
        return $"Finished searching for {title}.";
    }

    private static (string? Id, string? LibraryId, string StartedBy) Read(string? payloadJson, string? relatedId)
    {
        var id = relatedId;
        string? libraryId = null;
        var startedBy = TitleSearchJobs.FromBulkEditor;
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try
            {
                using var document = JsonDocument.Parse(payloadJson);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                    if (string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase))
                    {
                        id = string.IsNullOrWhiteSpace(value) ? id : value;
                    }
                    else if (string.Equals(property.Name, "libraryId", StringComparison.OrdinalIgnoreCase))
                    {
                        libraryId = value;
                    }
                    else if (string.Equals(property.Name, "startedBy", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                    {
                        startedBy = value;
                    }
                }
            }
            catch (JsonException)
            {
                // A payload that cannot be read falls back to the job's own related title.
            }
        }

        return (id, string.IsNullOrWhiteSpace(libraryId) ? null : libraryId, startedBy);
    }
}

public sealed class MovieTitleSearchJobHandler(
    IMediaStateRepository mediaStateRepository,
    ILibrariesRepository librariesRepository,
    IQualityRepository qualityRepository,
    IJobQueueRepository jobQueueRepository,
    IAcquisitionDecisionPipeline acquisitionPipeline,
    IDownloadClientGrabService downloadClientGrabService,
    IActivityFeedRepository activityFeedRepository,
    TimeProvider timeProvider,
    IMovieCatalogRepository movieCatalogRepository,
    IMediaTagStore? mediaTagStore = null,
    IReleasePreferencePlanRepository? releasePreferencePlanRepository = null,
    IPlatformSettingsRepository? platformSettingsRepository = null) : TitleSearchJobHandler(
        MediaKind.Movie,
        mediaStateRepository,
        librariesRepository,
        qualityRepository,
        jobQueueRepository,
        acquisitionPipeline,
        downloadClientGrabService,
        activityFeedRepository,
        timeProvider,
        (movieId, libraryId, triggerKind, outcome, now, nextEligibleUtc, lastSearchResult, releaseName, indexerName, detailsJson, token) =>
            movieCatalogRepository.RecordSearchAttemptAsync(
                movieId, libraryId, triggerKind, outcome, now, nextEligibleUtc, lastSearchResult, releaseName, indexerName, detailsJson, token),
        episodeTitles: null,
        mediaTagStore,
        releasePreferencePlanRepository,
        platformSettingsRepository)
{
    public override string JobType => TitleSearchJobs.Movie;
}

public sealed class SeriesTitleSearchJobHandler(
    IMediaStateRepository mediaStateRepository,
    ILibrariesRepository librariesRepository,
    IQualityRepository qualityRepository,
    IJobQueueRepository jobQueueRepository,
    IAcquisitionDecisionPipeline acquisitionPipeline,
    IDownloadClientGrabService downloadClientGrabService,
    IActivityFeedRepository activityFeedRepository,
    TimeProvider timeProvider,
    ISeriesCatalogRepository seriesCatalogRepository,
    IMediaTagStore? mediaTagStore = null,
    IReleasePreferencePlanRepository? releasePreferencePlanRepository = null,
    IPlatformSettingsRepository? platformSettingsRepository = null) : TitleSearchJobHandler(
        MediaKind.Series,
        mediaStateRepository,
        librariesRepository,
        qualityRepository,
        jobQueueRepository,
        acquisitionPipeline,
        downloadClientGrabService,
        activityFeedRepository,
        timeProvider,
        (seriesId, libraryId, triggerKind, outcome, now, nextEligibleUtc, lastSearchResult, releaseName, indexerName, detailsJson, token) =>
            seriesCatalogRepository.RecordSearchAttemptAsync(
                seriesId, null, libraryId, triggerKind, outcome, now, nextEligibleUtc, lastSearchResult, releaseName, indexerName, detailsJson, token),
        async (seriesId, token) => (await seriesCatalogRepository.GetInventoryDetailAsync(seriesId, token))?.EpisodeTitlesBySeason(),
        mediaTagStore,
        releasePreferencePlanRepository,
        platformSettingsRepository)
{
    public override string JobType => TitleSearchJobs.Series;
}
