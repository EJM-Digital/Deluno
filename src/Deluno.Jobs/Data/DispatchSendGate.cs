namespace Deluno.Jobs.Data;

/// <summary>
/// One send at a time per title and per release (#1192). The "is it already on
/// its way?" look (<see cref="DispatchCoverage"/>) and the send that follows it
/// are two steps, and two searches that run together - the search made when a
/// title is added and a "Search now" a minute later, or a feed-watch search and
/// a scheduled one - could each look before either had recorded its send, so
/// each sent the same release, to a different client. A search enters the gate
/// before it looks and leaves it after its send is recorded, so the second one
/// to arrive looks after the first has written down what it sent, and finds it.
///
/// <para>Deluno is one process with one set of databases, so a lock in memory is
/// the whole of it. Keys are taken in a fixed order so two searches that each
/// need two keys (an episode and its series) can never wait on each other.</para>
/// </summary>
public static class DispatchSendGate
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.Ordinal);

    /// <summary>The keys a search takes: each entity it is about to act on, and the release it is about to send.</summary>
    public static IReadOnlyList<string> KeysFor(string? releaseName, IEnumerable<string?> entityIds)
    {
        var keys = new List<string>();
        foreach (var id in entityIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                keys.Add($"entity:{id.Trim()}");
            }
        }

        if (!string.IsNullOrWhiteSpace(releaseName))
        {
            keys.Add($"release:{releaseName.Trim().ToLowerInvariant()}");
        }

        return keys;
    }

    /// <summary>Waits for every key to be free and holds them until the result is disposed.</summary>
    public static async Task<IDisposable> EnterAsync(IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        var ordered = keys.Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        var held = new List<SemaphoreSlim>(ordered.Length);
        try
        {
            foreach (var key in ordered)
            {
                var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(cancellationToken);
                held.Add(gate);
            }
        }
        catch
        {
            foreach (var gate in held)
            {
                gate.Release();
            }

            throw;
        }

        return new Hold(held);
    }

    /// <summary>Enters the gate for a release and the entities it is about to be sent for.</summary>
    public static Task<IDisposable> EnterAsync(string? releaseName, IEnumerable<string?> entityIds, CancellationToken cancellationToken)
        => EnterAsync(KeysFor(releaseName, entityIds), cancellationToken);

    private sealed class Hold(List<SemaphoreSlim> held) : IDisposable
    {
        private List<SemaphoreSlim>? held = held;

        public void Dispose()
        {
            var toRelease = Interlocked.Exchange(ref held, null);
            if (toRelease is null)
            {
                return;
            }

            for (var index = toRelease.Count - 1; index >= 0; index--)
            {
                toRelease[index].Release();
            }
        }
    }
}
