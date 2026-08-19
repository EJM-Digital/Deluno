namespace Deluno.Realtime;

/// <summary>
/// Wire shape for every realtime push: a monotonic sequence number, the
/// event name, an ISO-8601 timestamp, and the event's own payload.
/// </summary>
public sealed record RealtimeEnvelope(long Seq, string Name, string At, object? Data);

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
    /// The gap is beyond the resume window (or the client has no prior sequence).
    /// The client must refetch from REST and adopt the current sequence.
    /// </summary>
    ResyncRequired
}

public sealed record RealtimeResumeResult(RealtimeResumeStatus Status, IReadOnlyList<RealtimeEnvelope> Envelopes);

/// <summary>
/// Read side of the publisher's resume window, kept separate from the write
/// side so the hub only depends on what it needs to answer a resume request.
/// </summary>
public interface IRealtimeResumeSource
{
    RealtimeResumeResult Resume(long lastSeq);
}
