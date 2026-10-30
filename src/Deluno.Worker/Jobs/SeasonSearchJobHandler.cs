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
    IJobQueueRepository jobQueueRepository,
    Deluno.Worker.Dashboard.SearchesInFlight? searchesInFlight = null) : IJobHandler
{
    public string JobType => "series.search.season";

    /// <summary>The show is on Live's Searching lane, naming the season, for as long as this job runs (#1442).</summary>
    public async Task<string> HandleAsync(JobQueueItem job, CancellationToken cancellationToken)
    {
        if (searchesInFlight is null)
        {
            return await HandleCoreAsync(job, cancellationToken);
        }

        var asked = JobPayloads.ParseSeasonSearchPayload(job.PayloadJson);
        if (asked is not null && !string.IsNullOrWhiteSpace(asked.SeriesId))
        {
            await searchesInFlight.BeginAsync(job.Id, "series", asked.SeriesId, asked.SeriesTitle, "missing", $"Season {asked.SeasonNumber}");
        }

        try
        {
            return await HandleCoreAsync(job, cancellationToken);
        }
        finally
        {
            await searchesInFlight.EndAsync(job.Id);
        }
    }

    private async Task<string> HandleCoreAsync(JobQueueItem job, CancellationToken cancellationToken)
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

        // Every status but a genuine no-match already says why nothing was
        // planned again immediately: SeriesNotFound/SeasonNotFound have
        // nothing left to fall back to, NotSearchable/LibraryMissing have no
        // library to search the episodes against either, and Blocked and
        // EvidenceHeld have each just had SeasonSearchExecutor record
        // nextEligibleSearchUtc = now + retryDelayHours for every episode in
        // the season. A "held" Completed outcome (a candidate was found but
        // was not a safe automatic upgrade) records that same backoff. Falling
        // back to PlanEpisodeSearchesAsync here plans episode.search at "now"
        // regardless, which blew straight through the backoff the executor had
        // just written. Only "checked" — the search actually ran and found no
        // candidate at all — means there is nothing to wait on, so that is the
        // only case worth handing to the per-episode path right away (#646).
        var genuineNoMatch = result.Status == SeasonSearchStatus.Completed
            && string.Equals(result.Outcome, "checked", StringComparison.Ordinal);
        if (!result.Dispatched && genuineNoMatch)
        {
            // Every episode this job owns is still missing and still due, so
            // it goes back to being searched one at a time rather than waiting
            // for the next library cycle to notice (#646).
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
