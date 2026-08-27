using Deluno.Integrations.Metadata;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;

namespace Deluno.Worker.Jobs;

public sealed class MoviesCatalogRefreshJobHandler(
    IMetadataProvider metadataProvider,
    IMovieCatalogRepository movieCatalogRepository,
    IActivityFeedRepository activityFeedRepository) : IJobHandler
{
    public string JobType => "movies.catalog.refresh";

    // Same repair as the series twin (#245): this was a stub that reported
    // success without doing anything. It now runs the per-movie refresh so a
    // newly added movie picks up metadata and release dates without a manual
    // Refresh.
    public Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
        => new MoviesMetadataRefreshJobHandler(metadataProvider, movieCatalogRepository, activityFeedRepository)
            .HandleAsync(job, cancellationToken);
}
