using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Series.Data;

namespace Deluno.Worker.Jobs;

public sealed class SeriesQualityRecalculateJobHandler(
    ISeriesCatalogRepository seriesCatalogRepository,
    IActivityFeedRepository activityFeedRepository) : IJobHandler
{
    public string JobType => "series.quality.recalculate";

    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        var payload = JobPayloads.ParseQualityPayload(job.PayloadJson);
        if (payload is null)
        {
            return "Finished refreshing TV quality decisions.";
        }

        var updated = await seriesCatalogRepository.ReevaluateLibraryWantedStateAsync(
            payload.LibraryId,
            payload.CutoffQuality,
            payload.UpgradeUntilCutoff,
            payload.UpgradeUnknownItems,
            cancellationToken);

        await activityFeedRepository.RecordActivityAsync(
            "library.quality.recalculated",
            $"Deluno refreshed quality decisions for {payload.LibraryName} across {updated} TV show record{(updated == 1 ? "" : "s")}.",
            null,
            job.Id,
            "library",
            payload.LibraryId,
            cancellationToken);

        return $"Finished refreshing quality decisions for {payload.LibraryName}.";
    }
}
