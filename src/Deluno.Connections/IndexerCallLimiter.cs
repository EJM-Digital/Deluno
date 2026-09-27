using System.Collections.Concurrent;
using Deluno.Connections.Contracts;

namespace Deluno.Connections;

/// <summary>What kind of call is being counted against an indexer's own caps (#750).</summary>
public enum IndexerCallKind
{
    Query,
    Grab
}

/// <summary>The outcome of asking to make one more call against an indexer's configured cap (#750).</summary>
public sealed record IndexerCallLimitResult(bool Allowed, int? Limit, int Count, DateTimeOffset? ResetsAtUtc, string? Unit);

/// <summary>
/// Per-indexer Query Limit / Grab Limit caps (#750): in addition to
/// <see cref="Deluno.Infrastructure.Resilience.IOutboundRequestThrottle"/>'s
/// request pacing, a hard ceiling on how many searches or grabs an indexer
/// may see per day or per hour, so Deluno does not exceed a quota some
/// private trackers treat as a bannable offence even when pacing is
/// respected.
///
/// State is per process and in memory, the same deliberate choice
/// <c>OutboundRequestThrottle</c> makes: this only has to be right for calls
/// this process is about to make, and every count naturally starts fresh
/// alongside the process that would otherwise have made them.
/// </summary>
public interface IIndexerCallLimiter
{
    IndexerCallLimitResult TryConsumeQuery(IndexerItem indexer, DateTimeOffset now);

    IndexerCallLimitResult TryConsumeGrab(IndexerItem indexer, DateTimeOffset now);

    /// <summary>The current state without consuming a call, for the Health section (#750).</summary>
    IndexerCallLimitResult Peek(IndexerItem indexer, IndexerCallKind kind, DateTimeOffset now);

    /// <summary>
    /// #829: a single hourly search-call ceiling across every indexer
    /// combined, one instance-wide window rather than per indexer.
    /// <paramref name="enabled"/> false always allows (the setting is off
    /// by default, per #936); <paramref name="limit"/> is Deluno's
    /// MaxSearchesPerHour setting.
    /// </summary>
    IndexerCallLimitResult TryConsumeInstanceQuery(bool enabled, int limit, DateTimeOffset now);
}

public sealed class IndexerCallLimiter : IIndexerCallLimiter
{
    private sealed class Window
    {
        public readonly object Lock = new();
        public DateTimeOffset StartUtc;
        public int Count;
    }

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);

    public IndexerCallLimitResult TryConsumeQuery(IndexerItem indexer, DateTimeOffset now)
        => TryConsume(indexer, IndexerCallKind.Query, indexer.QueryLimit, now);

    public IndexerCallLimitResult TryConsumeGrab(IndexerItem indexer, DateTimeOffset now)
        => TryConsume(indexer, IndexerCallKind.Grab, indexer.GrabLimit, now);

    public IndexerCallLimitResult Peek(IndexerItem indexer, IndexerCallKind kind, DateTimeOffset now)
    {
        var limit = kind == IndexerCallKind.Query ? indexer.QueryLimit : indexer.GrabLimit;
        if (limit is not (> 0))
        {
            return new IndexerCallLimitResult(true, null, 0, null, null);
        }

        var duration = WindowDuration(indexer.LimitsUnit);
        var key = Key(indexer.Id, kind);
        if (!_windows.TryGetValue(key, out var window))
        {
            return new IndexerCallLimitResult(true, limit, 0, null, IndexerLimitsUnits.Normalize(indexer.LimitsUnit));
        }

        lock (window.Lock)
        {
            var resetsAtUtc = window.StartUtc + duration;
            var expired = now >= resetsAtUtc;
            var count = expired ? 0 : window.Count;
            return new IndexerCallLimitResult(
                expired || count < limit.Value,
                limit,
                count,
                expired ? null : resetsAtUtc,
                IndexerLimitsUnits.Normalize(indexer.LimitsUnit));
        }
    }

    private IndexerCallLimitResult TryConsume(IndexerItem indexer, IndexerCallKind kind, int? limit, DateTimeOffset now)
    {
        // Null or non-positive means unrestricted (#750), the same
        // empty-means-unlimited convention every other acquisition control
        // on an indexer already uses.
        if (limit is not (> 0))
        {
            return new IndexerCallLimitResult(true, null, 0, null, null);
        }

        var duration = WindowDuration(indexer.LimitsUnit);
        var unit = IndexerLimitsUnits.Normalize(indexer.LimitsUnit);
        var window = _windows.GetOrAdd(Key(indexer.Id, kind), _ => new Window { StartUtc = now, Count = 0 });

        lock (window.Lock)
        {
            if (now >= window.StartUtc + duration)
            {
                // The window has rolled over: this call starts a fresh one,
                // exactly the "count resetting at the next window boundary"
                // acceptance line asks for.
                window.StartUtc = now;
                window.Count = 0;
            }

            var resetsAtUtc = window.StartUtc + duration;
            if (window.Count >= limit.Value)
            {
                return new IndexerCallLimitResult(false, limit, window.Count, resetsAtUtc, unit);
            }

            window.Count++;
            return new IndexerCallLimitResult(true, limit, window.Count, resetsAtUtc, unit);
        }
    }

    private const string InstanceQueryKey = "instance:query-hourly";

    public IndexerCallLimitResult TryConsumeInstanceQuery(bool enabled, int limit, DateTimeOffset now)
    {
        if (!enabled || limit <= 0)
        {
            return new IndexerCallLimitResult(true, null, 0, null, null);
        }

        var window = _windows.GetOrAdd(InstanceQueryKey, _ => new Window { StartUtc = now, Count = 0 });
        lock (window.Lock)
        {
            if (now >= window.StartUtc + TimeSpan.FromHours(1))
            {
                window.StartUtc = now;
                window.Count = 0;
            }

            var resetsAtUtc = window.StartUtc + TimeSpan.FromHours(1);
            if (window.Count >= limit)
            {
                return new IndexerCallLimitResult(false, limit, window.Count, resetsAtUtc, "hour");
            }

            window.Count++;
            return new IndexerCallLimitResult(true, limit, window.Count, resetsAtUtc, "hour");
        }
    }

    private static TimeSpan WindowDuration(string? unit)
        => string.Equals(unit, "hour", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);

    private static string Key(string indexerId, IndexerCallKind kind) => $"{indexerId}:{kind}";
}

/// <summary>The "day" (default) / "hour" convention for a Limits Unit field (#750).</summary>
public static class IndexerLimitsUnits
{
    public const string Day = "day";
    public const string Hour = "hour";

    public static string Normalize(string? value)
        => string.Equals(value?.Trim(), Hour, StringComparison.OrdinalIgnoreCase) ? Hour : Day;
}
