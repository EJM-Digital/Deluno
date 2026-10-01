using System.Collections.Concurrent;
using Deluno.Contracts;

namespace Deluno.Realtime;

/// <summary>
/// Counts the connections joined to each realtime subject (#1097), so the
/// server can tell that someone has Live or Transfers open and quicken its
/// download readings, and stop building pushes nobody would see.
/// </summary>
public sealed class RealtimeWatcherTracker : IRealtimeWatchers
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    private int _connections;

    public int ConnectionCount => Volatile.Read(ref _connections);

    public void Connected() => Interlocked.Increment(ref _connections);

    public void Disconnected()
    {
        if (Interlocked.Decrement(ref _connections) < 0)
        {
            Interlocked.Exchange(ref _connections, 0);
        }
    }

    public int Count(string subject) => _counts.TryGetValue(subject, out var count) ? count : 0;

    public void Joined(string subject) => _counts.AddOrUpdate(subject, 1, (_, count) => count + 1);

    public void Left(string subject) => _counts.AddOrUpdate(subject, 0, (_, count) => Math.Max(0, count - 1));
}
