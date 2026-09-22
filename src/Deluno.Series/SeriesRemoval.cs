using Deluno.Media;
using Deluno.Series.Contracts;
using Deluno.Series.Data;

namespace Deluno.Series;

/// <summary>A TV show as <see cref="TitleRemovalService"/> removes it.</summary>
public static class SeriesRemoval
{
    public static TitleToRemove Target(SeriesListItem series, ISeriesCatalogRepository repository)
        => new(
            "tv",
            "series",
            series.Id,
            series.Title,
            series.StartYear,
            series.ImdbId,
            (libraryId, cancellationToken) => FilesOf(series.Id, libraryId, repository, cancellationToken),
            cancellationToken => repository.DeleteAsync(series.Id, cancellationToken),
            (scope, reason, cancellationToken) => repository.SettleImportRecoveryCasesAsync(scope, reason, cancellationToken));

    private static async IAsyncEnumerable<string> FilesOf(
        string seriesId,
        string libraryId,
        ISeriesCatalogRepository repository,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var file in repository.StreamTrackedFilesAsync(libraryId, cancellationToken))
        {
            if (string.Equals(file.SeriesId, seriesId, StringComparison.OrdinalIgnoreCase))
            {
                yield return file.FilePath;
            }
        }
    }
}
