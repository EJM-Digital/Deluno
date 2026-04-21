namespace Deluno.Series.Contracts;

public sealed record SeriesWantedItem(
    string SeriesId,
    string Title,
    int? StartYear,
    string? ImdbId,
    string LibraryId,
    string WantedStatus,
    string WantedReason,
    bool HasFile,
    bool QualityCutoffMet,
    DateTimeOffset? MissingSinceUtc,
    DateTimeOffset? LastSearchUtc,
    DateTimeOffset? NextEligibleSearchUtc,
    string? LastSearchResult,
    DateTimeOffset UpdatedUtc);
