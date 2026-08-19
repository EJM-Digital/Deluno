using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;

namespace Deluno.Worker.Jobs;

public sealed class MoviesQualityRecalculateJobHandler(
    IMovieCatalogRepository movieCatalogRepository,
    IActivityFeedRepository activityFeedRepository) : IJobHandler
{
    public string JobType => "movies.quality.recalculate";

    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        var payload = JobPayloads.ParseQualityPayload(job.PayloadJson);
        if (payload is null)
        {
            return "Finished refreshing movie quality decisions.";
        }

        var updated = await movieCatalogRepository.ReevaluateLibraryWantedStateAsync(
            payload.LibraryId,
            payload.CutoffQuality,
            payload.UpgradeUntilCutoff,
            payload.UpgradeUnknownItems,
            cancellationToken);

        await activityFeedRepository.RecordActivityAsync(
            "library.quality.recalculated",
            $"Deluno refreshed quality decisions for {payload.LibraryName} across {updated} movie record{(updated == 1 ? "" : "s")}.",
            null,
            job.Id,
            "library",
            payload.LibraryId,
            cancellationToken);

        return $"Finished refreshing quality decisions for {payload.LibraryName}.";
    }
}
