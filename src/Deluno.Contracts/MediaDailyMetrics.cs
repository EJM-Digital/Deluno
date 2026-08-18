namespace Deluno.Contracts;

/// <summary>
/// Per-day counts from one media engine, keyed by `yyyy-MM-dd`. Movies and TV
/// answer the same shape so the dashboard can add them together.
/// </summary>
public sealed record MediaDailyMetrics(
    /// <summary>Titles that already existed before the window started.</summary>
    int TitlesBeforeWindow,
    IReadOnlyDictionary<string, int> TitlesAdded,
    IReadOnlyDictionary<string, int> SearchesMatched,
    IReadOnlyDictionary<string, int> SearchesUnmatched,
    IReadOnlyDictionary<string, int> ImportFailures);
