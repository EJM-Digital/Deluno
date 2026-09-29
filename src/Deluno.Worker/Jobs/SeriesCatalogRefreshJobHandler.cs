using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Series.Data;

namespace Deluno.Worker.Jobs;

public sealed class SeriesCatalogRefreshJobHandler(
    IMetadataProvider metadataProvider,
    ISeriesCatalogRepository seriesCatalogRepository,
    IActivityFeedRepository activityFeedRepository,
    IJobScheduler jobScheduler,
    IAutoTaggingService? autoTagging = null) : IJobHandler
{
    public string JobType => "series.catalog.refresh";

    // The add flow enqueues this job so a new show has its metadata and
    // episode catalogue moments after it is created. It was a stub that
    // reported success without doing anything, which left every added show at
    // zero episodes until someone pressed Refresh metadata (#245). It now runs
    // the same per-series refresh the scheduled metadata job uses.
    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        var message = await new SeriesMetadataRefreshJobHandler(metadataProvider, seriesCatalogRepository, activityFeedRepository, autoTagging)
            .HandleAsync(job, cancellationToken);

        // The add endpoint's own targeted search rides along on this job's
        // payload instead of racing it to the queue (#645, #646 follow-up):
        // enqueued here, once the episode catalogue this refresh just learned
        // is on record, rather than by whichever of the two jobs a worker
        // happened to pick up first. Enqueued whether or not a metadata match
        // was found — a show search does not need episodes at all, and a
        // search that finds nothing because the catalogue stayed empty is no
        // worse than what ran before this fix.
        var payload = JobPayloads.ParseCatalogRefreshPayload(job.PayloadJson);
        if (payload?.PendingSearches is { Count: > 0 } pendingSearches)
        {
            foreach (var pending in pendingSearches)
            {
                await jobScheduler.EnqueueAsync(
                    new EnqueueJobRequest(
                        JobType: LibrarySearchJobTypes.For(pending.MediaType),
                        Source: "series",
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
                        RelatedEntityType: "series",
                        RelatedEntityId: pending.TargetEntityId),
                    cancellationToken);
            }
        }

        return message;
    }
}
