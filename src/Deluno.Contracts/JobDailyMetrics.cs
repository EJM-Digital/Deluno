namespace Deluno.Contracts;

/// <summary>Per-day job and dispatch counts, keyed by `yyyy-MM-dd`.</summary>
public sealed record JobDailyMetrics(
    IReadOnlyDictionary<string, int> JobsCompleted,
    IReadOnlyDictionary<string, int> JobsFailed,
    IReadOnlyDictionary<string, int> Grabs);
