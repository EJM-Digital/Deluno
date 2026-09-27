using Deluno.Jobs.Contracts;
using Deluno.Movies.Data;
using Deluno.Series.Data;

namespace Deluno.Worker.Services;

/// <summary>
/// Per-library tracked-file paths, read once per sharing pass rather than
/// once per hold (#819, #926) - what tells the real hardlink check which
/// library file a download's copy should still share data with.
/// </summary>
internal sealed class SharingTrackedFileCache(
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository)
{
    private readonly Dictionary<string, Dictionary<string, string>> _moviesByLibrary = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _episodesByLibrary = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ILookup<string, string>> _seriesFilesByLibrary = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The library file a dispatch's download should still hardlink to, or
    /// null when Deluno cannot resolve one (no library, no catalogue match,
    /// or a season pack whose show holds no tracked file in this library yet).
    /// </summary>
    public async Task<string?> ResolveAsync(DispatchCatalogueLink dispatch, string? libraryId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(libraryId) || string.IsNullOrWhiteSpace(dispatch.EntityId))
        {
            return null;
        }

        if (string.Equals(dispatch.EntityType, "movie", StringComparison.OrdinalIgnoreCase))
        {
            var movies = await MoviesAsync(libraryId, cancellationToken);
            return movies.GetValueOrDefault(dispatch.EntityId);
        }

        if (string.Equals(dispatch.EntityType, "episode", StringComparison.OrdinalIgnoreCase))
        {
            var episodes = await EpisodesAsync(libraryId, cancellationToken);
            if (episodes.TryGetValue(dispatch.EntityId, out var direct))
            {
                return direct;
            }
        }

        if (CatalogueImportNaming.IsShowGrab(dispatch))
        {
            // A season pack (or an episode Deluno has not indexed under its
            // own id): a representative file from the same show still
            // answers "did the library upgrade or delete what this download
            // shares", even though the download may hold several episodes.
            var seriesId = await CatalogueImportNaming.SeriesIdOfAsync(dispatch, seriesCatalogRepository, cancellationToken);
            if (seriesId is null)
            {
                return null;
            }

            var bySeries = await SeriesFilesAsync(libraryId, cancellationToken);
            return bySeries[seriesId].FirstOrDefault();
        }

        return null;
    }

    private async Task<Dictionary<string, string>> MoviesAsync(string libraryId, CancellationToken cancellationToken)
    {
        if (_moviesByLibrary.TryGetValue(libraryId, out var cached))
        {
            return cached;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var file in movieCatalogRepository.StreamTrackedFilesAsync(libraryId, cancellationToken))
        {
            map[file.MovieId] = file.FilePath;
        }

        _moviesByLibrary[libraryId] = map;
        return map;
    }

    private async Task<Dictionary<string, string>> EpisodesAsync(string libraryId, CancellationToken cancellationToken)
    {
        if (_episodesByLibrary.TryGetValue(libraryId, out var cached))
        {
            return cached;
        }

        await LoadSeriesAsync(libraryId, cancellationToken);
        return _episodesByLibrary[libraryId];
    }

    private async Task<ILookup<string, string>> SeriesFilesAsync(string libraryId, CancellationToken cancellationToken)
    {
        if (_seriesFilesByLibrary.TryGetValue(libraryId, out var cached))
        {
            return cached;
        }

        await LoadSeriesAsync(libraryId, cancellationToken);
        return _seriesFilesByLibrary[libraryId];
    }

    /// <summary>One stream, both indexes - a library's series files are read once per pass.</summary>
    private async Task LoadSeriesAsync(string libraryId, CancellationToken cancellationToken)
    {
        if (_episodesByLibrary.ContainsKey(libraryId))
        {
            return;
        }

        var episodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bySeries = new List<(string SeriesId, string FilePath)>();
        await foreach (var file in seriesCatalogRepository.StreamTrackedFilesAsync(libraryId, cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(file.EpisodeId))
            {
                episodes[file.EpisodeId] = file.FilePath;
            }

            bySeries.Add((file.SeriesId, file.FilePath));
        }

        _episodesByLibrary[libraryId] = episodes;
        _seriesFilesByLibrary[libraryId] = bySeries.ToLookup(entry => entry.SeriesId, entry => entry.FilePath, StringComparer.OrdinalIgnoreCase);
    }
}
