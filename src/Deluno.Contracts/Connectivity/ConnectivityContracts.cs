namespace Deluno.Contracts.Connectivity;

/// <summary>
/// Whether Deluno can currently reach the internet at all (#901).
///
/// <para>Judged by a real connectivity probe against several independent
/// hosts, never guessed from one download's own stall: a single bad release
/// looks exactly like an outage from inside that download, which is the
/// mistake this exists to avoid. "All of them failed at once" is what makes
/// it an outage rather than one host having a bad day.</para>
/// </summary>
public sealed record ConnectivityStatus(
    bool IsOutage,
    DateTimeOffset? SinceUtc,
    DateTimeOffset CheckedUtc,
    IReadOnlyList<string> CheckedHosts,
    string Message);

/// <summary>
/// The one place that knows whether the internet is down, so every judge that
/// would otherwise strike, blocklist, clean up, escalate a backoff or search
/// again can ask it first, and so a stall or strike window can subtract the
/// time an outage lasted instead of holding it against a download (#901).
/// </summary>
public interface IConnectivityMonitor
{
    /// <summary>
    /// Runs (or reuses a recent) connectivity probe and returns the current
    /// verdict, announcing the change to Activity the first time it is seen.
    /// </summary>
    Task<ConnectivityStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The most recently known verdict, for callers that cannot await a fresh
    /// probe on every decision (an in-memory circuit breaker's failure count,
    /// say). Reflects whatever <see cref="GetStatusAsync"/> last found;
    /// before the first probe ever runs it reads "no outage", since Deluno
    /// should not assume the world is broken before it has looked.
    /// </summary>
    ConnectivityStatus Current { get; }

    /// <summary>
    /// How much of the span between <paramref name="since"/> and
    /// <paramref name="until"/> happened while the internet was down, so a
    /// stall or strike window can skip it — the clock stops during an outage
    /// and picks back up exactly where it left off (#901).
    /// </summary>
    Task<TimeSpan> OutageOverlapAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken);
}

/// <summary>
/// "The internet has never gone down" - the safe default for a caller built
/// or tested before #901 that constructs a dependency directly instead of
/// through DI. Real DI always wires the real <see cref="ConnectivityMonitor"/>-
/// this exists so that omitting one is a no-op, not a compile error, and
/// every strike, cleanup and backoff rule behaves exactly as it did before
/// #901 until something actually reports an outage.
/// </summary>
public sealed class NullConnectivityMonitor : IConnectivityMonitor
{
    public static readonly NullConnectivityMonitor Instance = new();

    private static readonly ConnectivityStatus Connected = new(
        IsOutage: false,
        SinceUtc: null,
        CheckedUtc: DateTimeOffset.MinValue,
        CheckedHosts: [],
        Message: "Not checked yet.");

    public Task<ConnectivityStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Connected);

    public ConnectivityStatus Current => Connected;

    public Task<TimeSpan> OutageOverlapAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
        => Task.FromResult(TimeSpan.Zero);
}
