using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;

namespace Deluno.Worker.Jobs;

public sealed class MoviesCatalogRefreshJobHandler(
    IMetadataProvider metadataProvider,
    IMovieCatalogRepository movieCatalogRepository,
    IActivityFeedRepository activityFeedRepository,
    IJobScheduler jobScheduler) : IJobHandler
{
    public string JobType => "movies.catalog.refresh";

    // Same repair as the series twin (#245): this was a stub that reported
    // success without doing anything. It now runs the per-movie refresh so a
    // newly added movie picks up metadata and release dates without a manual
    // Refresh.
    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        var message = await new MoviesMetadataRefreshJobHandler(metadataProvider, movieCatalogRepository, activityFeedRepository)
            .HandleAsync(job, cancellationToken);

        // The add endpoint's own targeted search rides along on this job's
        // payload instead of racing it to the queue, the same fix as the
        // series side: enqueued here, once this refresh has had its chance to
        // fill in metadata (IMDb id, runtime) the search decision may need,
        // rather than by whichever of the two jobs a worker happened to pick
        // up first.
        var payload = JobPayloads.ParseCatalogRefreshPayload(job.PayloadJson);
        if (payload?.PendingSearches is { Count: > 0 } pendingSearches)
        {
            foreach (var pending in pendingSearches)
            {
                await jobScheduler.EnqueueAsync(
                    new EnqueueJobRequest(
                        JobType: LibrarySearchJobTypes.For(pending.MediaType),
                        Source: "movies",
                        PayloadJson: JsonSerializer.Serialize(new
                        {
                            libraryId = pending.LibraryId,
                            libraryName = pending.LibraryName,
                            mediaType = pending.MediaType,
                            checkMissing = true,
                            checkUpgrades = true,
                            maxItems = 1,
                            retryDelayHours = pending.RetryDelayHours,
                            triggeredBy = pending.TriggeredBy,
                            targetEntityId = pending.TargetEntityId
                        }),
                        RelatedEntityType: "movie",
                        RelatedEntityId: pending.TargetEntityId),
                    cancellationToken);
            }
        }

        return message;
    }
}
