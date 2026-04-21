namespace Deluno.Movies.Contracts;

public sealed record MovieWantedItem(
    string MovieId,
    string Title,
    int? ReleaseYear,
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
