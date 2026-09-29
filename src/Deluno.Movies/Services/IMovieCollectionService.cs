using Deluno.Movies.Contracts;

namespace Deluno.Movies.Services;

public interface IMovieCollectionService
{
    Task<MovieCollectionItem?> CreateOrUpdateAsync(
        CreateMovieCollectionRequest request,
        CancellationToken cancellationToken);

    Task<MovieCollectionSyncResult> SyncAsync(
        string collectionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// #998: changes the given settings on one or more collections, checking the
    /// quality profile, availability and movie library first (an ArgumentException
    /// carries a plain sentence) and keeping the stored profile and library names
    /// in step with the ids.
    /// </summary>
    /// <summary>
    /// #1056: stops following the given collections. Only the collection and its
    /// member list go; no movie is deleted, unmonitored or changed. Returns the
    /// collections that were removed.
    /// </summary>
    Task<IReadOnlyList<MovieCollectionItem>> RemoveManyAsync(
        IReadOnlyList<string> collectionIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MovieCollectionItem>> UpdateManyAsync(
        IReadOnlyList<string> collectionIds,
        UpdateMovieCollectionRequest changes,
        CancellationToken cancellationToken);
}
