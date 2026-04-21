namespace Deluno.Series.Contracts;

public sealed record SeriesWantedSummary(
    int TotalWanted,
    int MissingCount,
    int UpgradeCount,
    int WaitingCount,
    IReadOnlyList<SeriesWantedItem> RecentItems);
