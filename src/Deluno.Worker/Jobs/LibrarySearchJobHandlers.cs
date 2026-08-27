using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Quality.Data;
using Deluno.Series.Data;

namespace Deluno.Worker.Jobs;

/// <summary>
/// The two library-search job types, which differ only in the lane that leases
/// them.
///
/// One shared handler with two registrations, rather than two implementations:
/// the search behaviour is identical for both catalogues and the split exists
/// for *isolation*, so that a TV search never queues behind movie searches —
/// especially when those are stuck against an unresponsive indexer, which is
/// the case a single lane handles worst.
///
/// `JobHandlerRegistry` maps one handler to one job type, and the worker asserts
/// at startup that every registered handler has exactly one lane willing to
/// lease it. These two classes are what give it something to route.
/// </summary>
public sealed class MoviesLibrarySearchJobHandler(
    ILibrariesRepository librariesRepository,
    IQualityRepository qualityRepository,
    IJobQueueRepository jobQueueRepository,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    IAcquisitionDecisionPipeline acquisitionPipeline,
    IDownloadClientGrabService downloadClientGrabService,
    IActivityFeedRepository activityFeedRepository,
    TimeProvider timeProvider) : LibrarySearchJobHandler(
        librariesRepository,
        qualityRepository,
        jobQueueRepository,
        movieCatalogRepository,
        seriesCatalogRepository,
        acquisitionPipeline,
        downloadClientGrabService,
        activityFeedRepository,
        timeProvider)
{
    public override string JobType => LibrarySearchJobTypes.Movies;
}

/// <inheritdoc cref="MoviesLibrarySearchJobHandler"/>
public sealed class TvLibrarySearchJobHandler(
    ILibrariesRepository librariesRepository,
    IQualityRepository qualityRepository,
    IJobQueueRepository jobQueueRepository,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    IAcquisitionDecisionPipeline acquisitionPipeline,
    IDownloadClientGrabService downloadClientGrabService,
    IActivityFeedRepository activityFeedRepository,
    TimeProvider timeProvider) : LibrarySearchJobHandler(
        librariesRepository,
        qualityRepository,
        jobQueueRepository,
        movieCatalogRepository,
        seriesCatalogRepository,
        acquisitionPipeline,
        downloadClientGrabService,
        activityFeedRepository,
        timeProvider)
{
    public override string JobType => LibrarySearchJobTypes.Tv;
}
