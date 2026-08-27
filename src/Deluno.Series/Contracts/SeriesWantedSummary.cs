namespace Deluno.Series.Contracts;

public sealed record SeriesWantedSummary(
    int TotalWanted,
    int MissingCount,
    int UpgradeCount,
    /// <summary>
    /// Titles that have what the profile asked for. Named <c>Waiting</c> until
    /// #300 — the word the server set on a title that was finished, and that
    /// the front end described as "not searchable yet".
    /// </summary>
    int CoveredCount,
    /// <summary>Titles that are not out yet, so there is nothing to look for.</summary>
    int UpcomingCount,
    IReadOnlyList<SeriesWantedItem> RecentItems);
