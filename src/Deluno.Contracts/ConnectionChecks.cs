namespace Deluno.Contracts;

/// <summary>
/// Payload of <c>ConnectionChecked</c> (#1098): a real test of one connection just finished. It is pushed on the
/// Live and Dashboard subjects, by the regular check, Test all and every per-connection Test button alike, so
/// "Checked" on System and the tiles on Live agree. <c>Slow</c> is true only for a connection that answered.
/// </summary>
public sealed record ConnectionCheckedEvent(
    string Kind,
    string Id,
    string Name,
    bool Ok,
    bool Slow,
    string Message,
    string CheckedUtc);

/// <summary>The latest real check of one connection, as System shows it.</summary>
public sealed record ConnectionCheckItem(
    string Kind,
    string Id,
    string Name,
    bool Ok,
    bool Slow,
    string Message,
    DateTimeOffset CheckedUtc);

/// <summary>
/// Remembers and announces the result of a real connection test, whoever ran it. One path for the regular check,
/// Test all and the per-connection endpoints, so the pushed event, the stored "last checked" and the Activity line
/// can never disagree.
/// </summary>
public interface IConnectionCheckRecorder
{
    /// <param name="latencyMs">How long the answer took, when it came; a slow answer is flagged <c>Slow</c>.</param>
    Task<ConnectionCheckedEvent> RecordAsync(
        string kind,
        string id,
        string name,
        bool ok,
        int? latencyMs,
        string message,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ConnectionCheckItem>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>Runs the real test for a connection by kind and id; the one implementation every caller shares.</summary>
public interface IConnectionHealthTester
{
    /// <summary>Tests one connection now. Null when there is no such connection.</summary>
    Task<ConnectionCheckedEvent?> TestAsync(string kind, string id, CancellationToken cancellationToken);

    /// <summary>Tests every connection, a few at a time, reporting each as it finishes.</summary>
    Task<IReadOnlyList<ConnectionCheckedEvent>> TestAllAsync(CancellationToken cancellationToken);

    /// <summary>Tests only the connections nothing has checked or used since <paramref name="staleBeforeUtc"/>.</summary>
    Task<IReadOnlyList<ConnectionCheckedEvent>> TestStaleAsync(DateTimeOffset staleBeforeUtc, CancellationToken cancellationToken);
}

public static class ConnectionCheckRules
{
    /// <summary>How long a connection nobody has used is left before Deluno tests it for real.</summary>
    public static readonly TimeSpan RegularInterval = TimeSpan.FromMinutes(15);

    /// <summary>An answer slower than this is flagged slow even though it worked.</summary>
    public const int SlowAnswerMs = 3000;

    /// <summary>How many connections Test all and the regular check test at once.</summary>
    public const int Parallelism = 4;

    public static bool IsSlow(bool ok, int? latencyMs) => ok && latencyMs is >= SlowAnswerMs;
}
