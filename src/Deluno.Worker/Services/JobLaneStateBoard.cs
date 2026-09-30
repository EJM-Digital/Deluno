using System.Collections.Concurrent;

namespace Deluno.Worker.Services;

/// <summary>
/// What each background job lane is doing right now, kept in memory so the
/// Dashboard can show the lanes without asking the queue or the worker
/// anything. The lane supervisor and the heartbeat worker write to it; nothing
/// reads the database to fill it.
/// </summary>
public sealed class JobLaneStateBoard(TimeProvider timeProvider)
{
    public const string Running = "running";
    public const string Idle = "idle";
    public const string Failing = "failing";
    public const string Restarting = "restarting";
    public const string Disabled = "disabled";

    private readonly ConcurrentDictionary<string, LaneEntry> _lanes = new(StringComparer.OrdinalIgnoreCase);

    private sealed class LaneEntry
    {
        public int ActiveJobs;
        public string? Problem;
        public bool Restarting;
        public bool Disabled;
        public DateTimeOffset ChangedUtc;
    }

    private LaneEntry Entry(string lane) => _lanes.GetOrAdd(lane, _ => new LaneEntry { ChangedUtc = timeProvider.GetUtcNow() });

    /// <summary>A lane has been registered and is waiting for work.</summary>
    public void Started(string lane)
    {
        var entry = Entry(lane);
        entry.Restarting = false;
        entry.Disabled = false;
        entry.ChangedUtc = timeProvider.GetUtcNow();
    }

    public void MarkDisabled(string lane)
    {
        var entry = Entry(lane);
        entry.Disabled = true;
        entry.ChangedUtc = timeProvider.GetUtcNow();
    }

    /// <summary>The lane's loop ended and the supervisor is starting it again.</summary>
    public void MarkRestarting(string lane, string? error)
    {
        var entry = Entry(lane);
        entry.Restarting = true;
        entry.Problem = error;
        entry.ChangedUtc = timeProvider.GetUtcNow();
    }

    /// <summary>Passes keep failing (error) or have recovered (null).</summary>
    public void SetProblem(string lane, string? error)
    {
        var entry = Entry(lane);
        entry.Problem = error;
        entry.ChangedUtc = timeProvider.GetUtcNow();
    }

    public void JobStarted(string lane) => Interlocked.Increment(ref Entry(lane).ActiveJobs);

    public void JobFinished(string lane)
    {
        var entry = Entry(lane);
        if (Interlocked.Decrement(ref entry.ActiveJobs) < 0)
        {
            Interlocked.Exchange(ref entry.ActiveJobs, 0);
        }
    }

    public IReadOnlyList<JobLaneReading> Snapshot() => _lanes
        .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
        .Select(pair =>
        {
            var e = pair.Value;
            var state = e.Disabled ? Disabled
                : e.Restarting ? Restarting
                : e.Problem is not null ? Failing
                : Volatile.Read(ref e.ActiveJobs) > 0 ? Running
                : Idle;
            return new JobLaneReading(pair.Key, state, Math.Max(0, Volatile.Read(ref e.ActiveJobs)), e.Problem, e.ChangedUtc);
        })
        .ToArray();
}

public sealed record JobLaneReading(string Name, string State, int ActiveJobs, string? LastError, DateTimeOffset ChangedUtc);
