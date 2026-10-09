using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Series.Data;

namespace Deluno.Worker.Services;

/// <summary>
/// When a processor's "the download was gone" report asks nothing of anyone (#1186): Deluno removed that download
/// itself (an import's clean-up, a person's remove, the stall and failed-download rules, a title taken out of
/// Deluno), or the title already has the file that download was for. Used when the report arrives and again by the
/// settler for the rows still open, so a title that is imported later closes its row too.
/// </summary>
public static class SourceGoneRule
{
    /// <summary>The plain reason it needs nobody, or null when a person still has a question to answer.</summary>
    public static async Task<string?> SettledBecauseAsync(
        ProcessorHandoffItem handoff,
        IDownloadDispatchesRepository dispatches,
        IMovieCatalogRepository movies,
        ISeriesCatalogRepository series,
        CancellationToken cancellationToken)
    {
        var dispatch = await DispatchOfAsync(handoff, dispatches, cancellationToken);
        if (dispatch is null)
        {
            return null;
        }

        if (DelunoRemoved(dispatch))
        {
            return "Deluno removed that download itself, so there is nothing to do.";
        }

        if (!string.IsNullOrWhiteSpace(dispatch.EntityId))
        {
            var entityType = dispatch.EntityType.ToLowerInvariant();
            var holds = entityType switch
            {
                "movie" => await movies.GetTitleFileHoldsAsync([dispatch.EntityId], cancellationToken),
                "episode" => await series.GetTitleFileHoldsAsync([dispatch.EntityId], cancellationToken),
                _ => null
            };
            if (holds is not null
                && holds.ImportedByEntityId.TryGetValue(dispatch.EntityId, out var arrived)
                && !(arrived is { } at && at < handoff.CreatedUtc))
            {
                return "The title is in the library already, so there is nothing to do.";
            }
        }

        return null;
    }

    /// <summary>
    /// The send this hand-off belongs to (#1186): the latest of this release to this client, found by the client's id
    /// for the download or, while a new send has not recorded that id yet, by its release name. Never an older,
    /// archived send of the same hash: "Search for another copy" sends the same release again, and a gone download of
    /// that second send is not explained by Deluno having removed the first.
    /// </summary>
    internal static async Task<DownloadDispatchItem?> DispatchOfAsync(
        ProcessorHandoffItem handoff,
        IDownloadDispatchesRepository dispatches,
        CancellationToken cancellationToken)
    {
        var candidates = new List<DownloadDispatchItem>();
        if (await dispatches.FindDispatchesByClientItemIdsAsync(handoff.ClientId, [handoff.QueueItemId], cancellationToken) is { } byId)
        {
            candidates.AddRange(byId);
        }

        var (sent, _) = await dispatches.QueryDispatchesAsync(
            new DispatchQueryFilter { ClientId = handoff.ClientId, IncludeArchived = true, OmitLargeJson = true },
            new DispatchPaginationOptions { PageSize = 200 },
            cancellationToken);
        candidates.AddRange((sent ?? [])
            .Where(candidate => string.Equals(candidate.ReleaseName, handoff.ReleaseName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.DownloadClientId, handoff.ClientId, StringComparison.OrdinalIgnoreCase)));

        if (candidates.Count == 0
            && await dispatches.FindAnyDispatchByHashAsync(handoff.ClientId, handoff.QueueItemId, cancellationToken) is { } byHash)
        {
            candidates.Add(byHash);
        }

        return candidates
            .OrderByDescending(candidate => candidate.CreatedUtc)
            .FirstOrDefault();
    }

    /// <summary>Deluno ended this download: it archived the record, or imported it and cleaned up, or its own rules replaced it.</summary>
    private static bool DelunoRemoved(DownloadDispatchItem dispatch)
        => string.Equals(dispatch.Status, "archived", StringComparison.OrdinalIgnoreCase)
           || string.Equals(dispatch.ImportStatus, "imported", StringComparison.OrdinalIgnoreCase)
           || string.Equals(dispatch.ImportFailureCode, "health-remediation-applied", StringComparison.OrdinalIgnoreCase);
}
