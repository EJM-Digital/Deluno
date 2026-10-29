namespace Deluno.Movies.Contracts;

/// <summary>
/// A collection (a franchise, such as the Iron Man films) that a film in the library belongs to (#1425), as Radarr's
/// Collections tab shows it: every film of it, each marked held, wanted or missing. A collection is listed whether or
/// not it is followed; <see cref="FollowedCollectionId"/> says which.
/// </summary>
public sealed record LibraryCollectionItem(
    string ProviderId,
    string Name,
    string? Overview,
    string? PosterUrl,
    string? FollowedCollectionId,
    bool Following,
    int HeldCount,
    int WantedCount,
    int MissingCount,
    IReadOnlyList<LibraryCollectionMember> Members,
    /// <summary>Whether the provider's own member list could be read. When it could not, only the films the library holds are listed.</summary>
    bool MembersKnown,
    /// <summary>Why this collection is suggested, or null when it is not.</summary>
    string? Why = null);

public static class LibraryCollectionStatuses
{
    /// <summary>The film is in the library with a file.</summary>
    public const string Held = "held";

    /// <summary>The film is in the library and monitored, but has no file yet.</summary>
    public const string Wanted = "wanted";

    /// <summary>The film is not in the library, or is there but not monitored and has no file.</summary>
    public const string Missing = "missing";
}

public sealed record LibraryCollectionMember(
    string ProviderId,
    string Title,
    int? ReleaseYear,
    string? PosterUrl,
    string Status,
    string? LocalMovieId,
    bool IsExcluded);

/// <summary>Every collection a held film belongs to, and the ones worth completing first.</summary>
public sealed record LibraryCollectionsResponse(
    IReadOnlyList<LibraryCollectionItem> Collections,
    IReadOnlyList<LibraryCollectionItem> Recommended);

public sealed record AddMissingCollectionFilmsRequest(string? LibraryId = null);

public sealed record AddMissingCollectionFilmsResult(
    string CollectionId,
    string CollectionName,
    int AddedCount,
    string Message,
    bool SearchRequested);
