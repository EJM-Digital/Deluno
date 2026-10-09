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
        var dispatch = await dispatches.FindAnyDispatchByHashAsync(handoff.ClientId, handoff.QueueItemId, cancellationToken);
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

    /// <summary>Deluno ended this download: it archived the record, or imported it and cleaned up, or its own rules replaced it.</summary>
    private static bool DelunoRemoved(DownloadDispatchItem dispatch)
        => string.Equals(dispatch.Status, "archived", StringComparison.OrdinalIgnoreCase)
           || string.Equals(dispatch.ImportStatus, "imported", StringComparison.OrdinalIgnoreCase)
           || string.Equals(dispatch.ImportFailureCode, "health-remediation-applied", StringComparison.OrdinalIgnoreCase);
}
