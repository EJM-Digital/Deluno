namespace Deluno.Realtime;

/// <summary>
/// Wire shape for every realtime push: a monotonic sequence number, the
/// event name, an ISO-8601 timestamp, and the event's own payload.
/// </summary>
public sealed record RealtimeEnvelope(long Seq, string Name, string Subject, string At, object? Data);

/// <summary>
/// Outcome of a client asking to resume from its last known sequence number.
/// </summary>
public enum RealtimeResumeStatus
{
    /// <summary>The client's last sequence is already current; nothing to replay.</summary>
    CaughtUp,

    /// <summary>The gap was inside the resume window; the envelopes replay it.</summary>
    Replayed,

    /// <summary>
    /// The gap is beyond the resume window, the client has no prior sequence, or its sequence came from another server run.
    /// The client must refetch from REST and adopt the current sequence.
    /// </summary>
    ResyncRequired
}

/// <summary>
/// <paramref name="InstanceId"/> names the server run that answered. A page keeps it with its sequence number: the
/// sequence starts again at 0 when Deluno restarts, so a number only means something together with its instance (#1315).
/// </summary>
public sealed record RealtimeResumeResult(RealtimeResumeStatus Status, IReadOnlyList<RealtimeEnvelope> Envelopes, string InstanceId = "");

/// <summary>
/// Read side of the publisher's resume window, kept separate from the write
/// side so the hub only depends on what it needs to answer a resume request.
/// </summary>
public interface IRealtimeResumeSource
{
    /// <summary>
    /// <paramref name="instanceId"/> is the instance the client's sequence came from. A different one always means
    /// ResyncRequired; null skips the check, for callers inside this process.
    /// </summary>
    RealtimeResumeResult Resume(long lastSeq, IReadOnlyCollection<string> subjects, string? instanceId = null);

    /// <summary>Names this run of the server; new on every start, because the sequence restarts at 0 (#1315).</summary>
    string InstanceId { get; }

    /// <summary>The sequence of the newest event published so far (0 before any).</summary>
    long CurrentSequence { get; }
}
