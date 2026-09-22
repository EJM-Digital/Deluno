using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;

namespace Deluno.Movies;

/// <summary>A film as <see cref="TitleRemovalService"/> removes it.</summary>
public static class MovieRemoval
{
    public static TitleToRemove Target(MovieListItem movie, IMovieCatalogRepository repository)
        => new(
            "movies",
            "movie",
            movie.Id,
            movie.Title,
            movie.ReleaseYear,
            movie.ImdbId,
            (libraryId, cancellationToken) => FilesOf(movie.Id, libraryId, repository, cancellationToken),
            cancellationToken => repository.DeleteAsync(movie.Id, cancellationToken),
            (scope, reason, cancellationToken) => repository.SettleImportRecoveryCasesAsync(scope, reason, cancellationToken));

    private static async IAsyncEnumerable<string> FilesOf(
        string movieId,
        string libraryId,
        IMovieCatalogRepository repository,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var file in repository.StreamTrackedFilesAsync(libraryId, cancellationToken))
        {
            if (string.Equals(file.MovieId, movieId, StringComparison.OrdinalIgnoreCase))
            {
                yield return file.FilePath;
            }
        }
    }
}
