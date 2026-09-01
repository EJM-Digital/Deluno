namespace Deluno.Movies.Contracts;

/// <summary>
/// A provider-backed movie franchise tracked against one movie library.
/// Missing is calculated from the membership table, so it includes films
/// Deluno has never added as well as films that were removed from the library.
/// </summary>
public sealed record MovieCollectionItem(
    string Id,
    string Provider,
    string ProviderId,
    string Name,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    string LibraryId,
    string LibraryName,
    string RootPath,
    bool Monitored,
    bool MonitorMovies,
    string? QualityProfileId,
    string? QualityProfileName,
    string MinimumAvailability,
    bool SearchOnAdd,
    int MemberCount,
    int HeldCount,
    int MissingCount,
    DateTimeOffset? LastSyncedUtc,
    DateTimeOffset? NextSyncUtc,
    string? LastSyncError,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record MovieCollectionMemberItem(
    string CollectionId,
    string ProviderId,
    string Title,
    int? ReleaseYear,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    string? ExternalUrl,
    string? ImdbId,
    string? LocalMovieId,
    bool IsExcluded,
    DateTimeOffset UpdatedUtc);

public sealed record CreateMovieCollectionRequest(
    string? ProviderId,
    string? LibraryId,
    bool Monitored = false,
    bool MonitorMovies = true,
    string? QualityProfileId = null,
    string? MinimumAvailability = null,
    bool SearchOnAdd = false);

public sealed record UpdateMovieCollectionRequest(
    bool? Monitored = null,
    bool? MonitorMovies = null,
    string? QualityProfileId = null,
    string? MinimumAvailability = null,
    bool? SearchOnAdd = null);

public sealed record MovieCollectionSyncResult(
    string CollectionId,
    int MemberCount,
    int AddedCount,
    int LinkedCount,
    int ExcludedCount,
    bool SearchRequested,
    string Message);
