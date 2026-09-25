using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Series.Services;

namespace Deluno.Worker.Jobs;

/// <summary>
/// Runs the whole-season search the automatic library cycle planned for a
/// season it found entirely missing and fully aired (#646).
///
/// <para>Executes through <see cref="SeasonSearchExecutor"/> — the same code
/// the "Search season" button runs — so a pack is picked and graded against
/// every installed episode file exactly the way the manual path already does.
/// The one thing this handler owns that the button does not: if the search
/// ends without a grab, nothing was left unsearched. The episodes this job was
/// planned for still need their own search, queued here rather than waiting
/// for the next library cycle to notice they are still missing.</para>
/// </summary>
public sealed class SeasonSearchJobHandler(
    ISeasonSearchExecutor seasonSearchExecutor,
    IJobQueueRepository jobQueueRepository) : IJobHandler
{
    public string JobType => "series.search.season";

    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        var payload = JobPayloads.ParseSeasonSearchPayload(job.PayloadJson);
        if (payload is null || string.IsNullOrWhiteSpace(payload.SeriesId) || payload.Episodes.Count == 0)
        {
            return "Finished searching for season.";
        }

        var result = await seasonSearchExecutor.ExecuteAsync(
            payload.SeriesId,
            payload.SeasonNumber,
            triggeredBy: "automatic-season",
            activityClause: "was searched by the automatic library cycle.",
            cancellationToken);

        // Nothing to fall back to if the series or season no longer exists —
        // the episodes this job was planned for are gone with it.
        var seasonGone = result.Status is SeasonSearchStatus.SeriesNotFound or SeasonSearchStatus.SeasonNotFound;
        if (!result.Dispatched && !seasonGone)
        {
            // No acceptable pack: held on evidence, blocked on configuration,
            // or the search simply found nothing. Whatever the reason, every
            // episode this job owns is still missing and still due, so it goes
            // back to being searched one at a time rather than waiting for the
            // next library cycle to notice (#646).
            var fallback = payload.Episodes
                .Select(episode => new EpisodeSearchPlanItem(
                    episode.EpisodeId,
                    payload.SeriesId,
                    payload.SeasonNumber,
                    episode.EpisodeNumber,
                    episode.Title))
                .ToArray();
            await jobQueueRepository.PlanEpisodeSearchesAsync(payload.LibraryId, fallback, cancellationToken);
            return $"Finished searching season S{payload.SeasonNumber:D2} of {payload.SeriesTitle}: no season pack was grabbed, {fallback.Length} episode search(es) queued.";
        }

        return $"Finished searching season S{payload.SeasonNumber:D2} of {payload.SeriesTitle}.";
    }
}
