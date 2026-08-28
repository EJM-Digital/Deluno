using Deluno.Jobs.Data;

namespace Deluno.Media;

/// <summary>
/// Which titles still have a download actually happening.
///
/// <para><b>One question, named.</b> The reconciler needs exactly this and
/// nothing else, and <see cref="IDownloadDispatchesRepository"/> is a twenty-odd
/// method surface covering grabs, detection, timelines, circuit breakers,
/// archiving and retry windows. Depending on the whole of it to ask one question
/// makes the dependency unreadable and every test of the reconciler a twenty-
/// method stub that says nothing about what is being tested.</para>
/// </summary>
public interface ILiveDownloadLookup
{
    /// <summary>
    /// The entity ids of dispatches that have not reached a terminal state.
    ///
    /// <para>Under-reporting is the dangerous direction: a live download missed
    /// from this list is a title wrongly put back on the work list and grabbed a
    /// second time. Over-reporting merely delays a correction until the
    /// seven-day backstop.</para>
    /// </summary>
    Task<IReadOnlyList<string>> ListEntityIdsStillDownloadingAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The real answer, from the dispatch table.
/// </summary>
public sealed class DispatchLiveDownloadLookup(IDownloadDispatchesRepository dispatches) : ILiveDownloadLookup
{
    /// <summary>
    /// Deliberately generous, for the reason above: bounding this too tightly
    /// causes duplicate grabs rather than a slow pass.
    /// </summary>
    private const int Limit = 5_000;

    public async Task<IReadOnlyList<string>> ListEntityIdsStillDownloadingAsync(CancellationToken cancellationToken)
    {
        var unresolved = await dispatches.FindUnresolvedDispatchesAsync(
            minAgeMinutes: 0,
            clientId: null,
            limit: Limit,
            cancellationToken);

        return [.. unresolved
            .Select(dispatch => dispatch.EntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)];
    }
}
