using Deluno.Contracts;

namespace Deluno.Worker.Dashboard;

/// <summary>
/// The titles Deluno is searching for at this moment (#1442): a search a person started, a title just added, the
/// title a search cycle is on in its current pass, a Seerr request's search. A title is here from the moment its
/// indexers are asked until its search ends, with a release sent (the Grabbed card takes over) or with nothing.
/// Wanted titles that are only waiting for their turn are not here. Each change is pushed as
/// <c>SearchesInFlightChanged</c> with the whole list, and the Live extras carry the same list for a first load.
/// </summary>
public sealed class SearchesInFlight(TimeProvider timeProvider, IRealtimeEventPublisher? realtime = null)
{
    private readonly object _lock = new();
    private readonly SemaphoreSlim _pushOrder = new(1, 1);
    // One running job works on one title at a time, so a job has at most one entry: the next title replaces it.
    private readonly Dictionary<string, SearchingTitle> _byJob = new(StringComparer.Ordinal);

    public IReadOnlyList<SearchingTitle> Snapshot()
    {
        lock (_lock)
        {
            return _byJob.Values.OrderBy(item => item.StartedUtc, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>"Checking 5 indexers" while the indexers are asked.</summary>
    public static string CheckingLabel(int indexers)
        => indexers <= 0 ? "Looking for it" : indexers == 1 ? "Checking 1 indexer" : $"Checking {indexers} indexers";

    public Task BeginAsync(string jobId, string entityType, string entityId, string title, string startedBy, int indexers)
        => ChangeAsync(() =>
        {
            _byJob[jobId] = new SearchingTitle(entityType, entityId, title, startedBy, CheckingLabel(indexers), timeProvider.GetUtcNow().ToString("O"));
            return true;
        });

    /// <summary>The job's current title is done (a release was sent, or nothing good was found).</summary>
    public Task EndAsync(string jobId) => ChangeAsync(() => _byJob.Remove(jobId));

    private async Task ChangeAsync(Func<bool> change)
    {
        if (realtime is null)
        {
            lock (_lock) { change(); }
            return;
        }

        // Changes are pushed in the order they were made, so a screen never ends on an older list.
        await _pushOrder.WaitAsync();
        try
        {
            bool changed;
            IReadOnlyList<SearchingTitle> list;
            lock (_lock)
            {
                changed = change();
                list = _byJob.Values.OrderBy(item => item.StartedUtc, StringComparer.Ordinal).ToArray();
            }

            if (changed)
            {
                await realtime.PublishSafelyAsync(
                    publisher => publisher.PublishSearchesInFlightChangedAsync(
                        new SearchesInFlightChangedEvent(list, timeProvider.GetUtcNow().ToString("O")),
                        CancellationToken.None));
            }
        }
        finally
        {
            _pushOrder.Release();
        }
    }
}
