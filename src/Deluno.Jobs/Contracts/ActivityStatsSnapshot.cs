namespace Deluno.Jobs.Contracts;

/// <summary>
/// Aggregated counts over a time window, for an external dashboard widget
/// (Homepage, Homarr) to poll and for the Dashboard's own summary (#827).
///
/// The Activity feed is a human-readable event log; this is the same data,
/// counted, so a machine can ask "how much happened" without parsing
/// sentences. Deluno is single-user, so there is no per-caller scoping here —
/// every count is simply everything this instance logged in the window.
/// </summary>
public sealed record ActivityStatsSnapshot(
    DateTimeOffset SinceUtc,
    DateTimeOffset GeneratedUtc,
    /// <summary>The window's own length, so a caller need not re-derive it from the two timestamps above.</summary>
    int WindowHours,
    int SearchesRun,
    int DownloadsStarted,
    int ImportsCompleted,
    int CleanupRemovals,
    int RetentionRemovals);
