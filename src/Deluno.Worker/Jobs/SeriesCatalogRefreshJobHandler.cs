using Deluno.Integrations.Metadata;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Series.Data;

namespace Deluno.Worker.Jobs;

public sealed class SeriesCatalogRefreshJobHandler(
    IMetadataProvider metadataProvider,
    ISeriesCatalogRepository seriesCatalogRepository,
    IActivityFeedRepository activityFeedRepository) : IJobHandler
{
    public string JobType => "series.catalog.refresh";

    // The add flow enqueues this job so a new show has its metadata and
    // episode catalogue moments after it is created. It was a stub that
    // reported success without doing anything, which left every added show at
    // zero episodes until someone pressed Refresh metadata (#245). It now runs
    // the same per-series refresh the scheduled metadata job uses.
    public Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
        => new SeriesMetadataRefreshJobHandler(metadataProvider, seriesCatalogRepository, activityFeedRepository)
            .HandleAsync(job, cancellationToken);
}
