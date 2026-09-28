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
            (scope, reason, cancellationToken) => repository.SettleImportRecoveryCasesAsync(scope, reason, cancellationToken),
            // #947: the most common TV grab (an automatic per-episode search)
            // is recorded under its episode's own id, not the series' - so
            // removing a series has to reach those dispatches too.
            cancellationToken => RelatedEpisodeIdsOf(series.Id, repository, cancellationToken));

    private static async Task<IReadOnlyList<string>> RelatedEpisodeIdsOf(
        string seriesId,
        ISeriesCatalogRepository repository,
        CancellationToken cancellationToken)
    {
        var inventory = await repository.GetInventoryDetailAsync(seriesId, cancellationToken);
        return inventory is null
            ? []
            : inventory.Episodes.Select(episode => episode.EpisodeId).ToArray();
    }

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
