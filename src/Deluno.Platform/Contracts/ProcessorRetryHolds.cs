using System.Collections.Concurrent;

namespace Deluno.Platform.Contracts;

/// <summary>
/// Where Deluno remembers that a processor asked it to wait (<see cref="ProcessorRetryAfter"/>, #1266). One instance for the whole program, so a
/// 503 Weir gave the hand-off sender holds back the status poll, the cancel, the capability read and the outcome report as well: a busy
/// Weir is busy for all of them. Kept by connection id, in memory: a restart forgets it, and the first call after one simply asks again.
/// </summary>
public sealed class ProcessorRetryHolds
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _until = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remembers that nothing is to be sent to the connection before <paramref name="until"/> (a later time replaces an earlier one, never the reverse).</summary>
    public void Hold(string connectionId, DateTimeOffset until) => _until.AddOrUpdate(connectionId, until, (_, current) => until > current ? until : current);

    /// <summary>How long is left of a hold, or null when there is none.</summary>
    public TimeSpan? Remaining(string connectionId, DateTimeOffset now)
    {
        if (!_until.TryGetValue(connectionId, out var until))
        {
            return null;
        }

        if (until <= now)
        {
            _until.TryRemove(connectionId, out _);
            return null;
        }

        return until - now;
    }
}
