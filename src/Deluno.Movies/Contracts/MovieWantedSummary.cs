namespace Deluno.Movies.Contracts;

public sealed record MovieWantedSummary(
    int TotalWanted,
    int MissingCount,
    int UpgradeCount,
    int WaitingCount,
    IReadOnlyList<MovieWantedItem> RecentItems);
