namespace Deluno.Series.Contracts;

public sealed record EpisodeSearchEligibilityItem(
    string EpisodeId,
    string SeriesId,
    int SeasonNumber,
    int EpisodeNumber,
    string? Title,
    DateTimeOffset? LastSearchUtc,
    DateTimeOffset? NextEligibleSearchUtc,
    /// <summary>
    /// "missing" or "upgrade" (#830) -- lets a combined, unscoped cycle cap
    /// each kind against its own budget the same way the show-level lists
    /// already do, instead of one list eating the other's share.
    /// </summary>
    string WantedStatus = "missing");
