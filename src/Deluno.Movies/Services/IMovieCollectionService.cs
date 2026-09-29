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
    Task<IReadOnlyList<MovieCollectionItem>> UpdateManyAsync(
        IReadOnlyList<string> collectionIds,
        UpdateMovieCollectionRequest changes,
        CancellationToken cancellationToken);
}
