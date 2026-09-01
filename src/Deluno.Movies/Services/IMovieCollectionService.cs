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
}
