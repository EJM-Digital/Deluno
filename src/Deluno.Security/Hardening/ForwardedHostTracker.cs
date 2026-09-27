using System.Collections.Concurrent;

namespace Deluno.Security.Hardening;

/// <summary>
/// The distinct <c>X-Forwarded-Host</c> values Deluno has actually seen on a
/// real request (#939) -- so a reverse proxy or Docker host in front of it
/// shows up as a tick-able Allowed Hosts candidate instead of something the
/// person has to already know and type in.
/// </summary>
public interface IForwardedHostTracker
{
    void Record(string host);

    IReadOnlyCollection<string> Seen { get; }
}

public sealed class ForwardedHostTracker : IForwardedHostTracker
{
    private readonly ConcurrentDictionary<string, byte> seen = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string host)
    {
        var trimmed = AllowedHostMatcher.StripPort(host);
        if (trimmed.Length == 0) return;
        seen.TryAdd(trimmed, 0);
    }

    public IReadOnlyCollection<string> Seen => seen.Keys.ToArray();
}
