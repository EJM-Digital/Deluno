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
    DateTimeOffset UpdatedUtc,
    /// <summary>Comma-separated tag names given to every movie this collection adds (#1046). Empty gives none.</summary>
    string Tags = "");

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
    bool SearchOnAdd = false,
    string? Tags = null);

public sealed record UpdateMovieCollectionRequest(
    bool? Monitored = null,
    bool? MonitorMovies = null,
    string? QualityProfileId = null,
    string? MinimumAvailability = null,
    bool? SearchOnAdd = null,
    string? Tags = null,
    /// <summary>Move the collection to another movie library, which is its root folder (#998, Radarr's RootFolderPath).</summary>
    string? LibraryId = null);

/// <summary>#998: change the same settings on several collections at once, as Radarr's collection editor does.</summary>
public sealed record BulkUpdateMovieCollectionsRequest(
    IReadOnlyList<string>? CollectionIds,
    bool? Monitored = null,
    bool? MonitorMovies = null,
    string? QualityProfileId = null,
    string? MinimumAvailability = null,
    bool? SearchOnAdd = null,
    string? LibraryId = null);

/// <summary>Names looked up for the ids an update carries, so the stored copy never goes stale.</summary>
public sealed record MovieCollectionResolvedTargets(
    string? QualityProfileName = null,
    string? LibraryName = null,
    string? RootPath = null);

public sealed record MovieCollectionSyncResult(
    string CollectionId,
    int MemberCount,
    int AddedCount,
    int LinkedCount,
    int ExcludedCount,
    bool SearchRequested,
    string Message);
