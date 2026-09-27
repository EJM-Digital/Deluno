using Deluno.Jobs.Contracts;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #819/#926's per-title file resolution: what the real hardlink check needs
/// to know which library file a download's copy should still share data
/// with, read once per library rather than once per hold.
/// </summary>
public sealed class SharingTrackedFileCacheTests
{
    private static DispatchCatalogueLink Dispatch(string entityType, string entityId, string libraryId = "lib-1")
        => new("dispatch-1", entityType, entityId, LibraryId: libraryId);

    [Fact]
    public async Task A_movie_resolves_its_own_tracked_file()
    {
        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repo => repo.StreamTrackedFilesAsync("lib-1", It.IsAny<CancellationToken>()))
            .Returns(Files(("m1", @"C:\Library\Movies\Nosferatu.mkv")));
        var series = new Mock<ISeriesCatalogRepository>();
        var cache = new SharingTrackedFileCache(movies.Object, series.Object);

        var path = await cache.ResolveAsync(Dispatch("movie", "m1"), "lib-1", CancellationToken.None);

        Assert.Equal(@"C:\Library\Movies\Nosferatu.mkv", path);
    }

    [Fact]
    public async Task An_episode_resolves_directly_by_its_own_id()
    {
        var movies = new Mock<IMovieCatalogRepository>();
        var series = new Mock<ISeriesCatalogRepository>();
        series.Setup(repo => repo.StreamTrackedFilesAsync("lib-1", It.IsAny<CancellationToken>()))
            .Returns(EpisodeFiles(("s1", "e1", @"C:\Library\TV\Show\S01E01.mkv")));
        var cache = new SharingTrackedFileCache(movies.Object, series.Object);

        var path = await cache.ResolveAsync(Dispatch("episode", "e1"), "lib-1", CancellationToken.None);

        Assert.Equal(@"C:\Library\TV\Show\S01E01.mkv", path);
    }

    [Fact]
    public async Task A_season_pack_falls_back_to_a_representative_file_from_the_same_show()
    {
        var movies = new Mock<IMovieCatalogRepository>();
        var series = new Mock<ISeriesCatalogRepository>();
        series.Setup(repo => repo.StreamTrackedFilesAsync("lib-1", It.IsAny<CancellationToken>()))
            .Returns(EpisodeFiles(("s1", "e1", @"C:\Library\TV\Show\S01E01.mkv"), ("s1", "e2", @"C:\Library\TV\Show\S01E02.mkv")));
        var cache = new SharingTrackedFileCache(movies.Object, series.Object);

        var path = await cache.ResolveAsync(Dispatch("series", "s1"), "lib-1", CancellationToken.None);

        Assert.Equal(@"C:\Library\TV\Show\S01E01.mkv", path);
    }

    [Fact]
    public async Task An_episode_id_Deluno_has_not_indexed_falls_back_through_its_series()
    {
        var movies = new Mock<IMovieCatalogRepository>();
        var series = new Mock<ISeriesCatalogRepository>();
        series.Setup(repo => repo.StreamTrackedFilesAsync("lib-1", It.IsAny<CancellationToken>()))
            .Returns(EpisodeFiles(("s1", "e1", @"C:\Library\TV\Show\S01E01.mkv")));
        series.Setup(repo => repo.GetEpisodeSeriesIdAsync("unknown-episode", It.IsAny<CancellationToken>())).ReturnsAsync("s1");
        var cache = new SharingTrackedFileCache(movies.Object, series.Object);

        var path = await cache.ResolveAsync(Dispatch("episode", "unknown-episode"), "lib-1", CancellationToken.None);

        Assert.Equal(@"C:\Library\TV\Show\S01E01.mkv", path);
    }

    [Fact]
    public async Task No_library_resolves_nothing()
    {
        var movies = new Mock<IMovieCatalogRepository>(MockBehavior.Strict);
        var series = new Mock<ISeriesCatalogRepository>(MockBehavior.Strict);
        var cache = new SharingTrackedFileCache(movies.Object, series.Object);

        var path = await cache.ResolveAsync(Dispatch("movie", "m1"), null, CancellationToken.None);

        Assert.Null(path);
    }

    [Fact]
    public async Task A_library_is_only_streamed_once_across_several_lookups()
    {
        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repo => repo.StreamTrackedFilesAsync("lib-1", It.IsAny<CancellationToken>()))
            .Returns(Files(("m1", @"C:\Library\Movies\A.mkv"), ("m2", @"C:\Library\Movies\B.mkv")));
        var series = new Mock<ISeriesCatalogRepository>();
        var cache = new SharingTrackedFileCache(movies.Object, series.Object);

        await cache.ResolveAsync(Dispatch("movie", "m1"), "lib-1", CancellationToken.None);
        await cache.ResolveAsync(Dispatch("movie", "m2"), "lib-1", CancellationToken.None);

        movies.Verify(repo => repo.StreamTrackedFilesAsync("lib-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static async IAsyncEnumerable<MovieTrackedFileItem> Files(params (string MovieId, string Path)[] files)
    {
        foreach (var (movieId, path) in files)
        {
            var tracked = (MovieTrackedFileItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MovieTrackedFileItem));
            yield return tracked with { MovieId = movieId, LibraryId = "lib-1", FilePath = path };
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<Deluno.Series.Contracts.SeriesTrackedFileItem> EpisodeFiles(params (string SeriesId, string EpisodeId, string Path)[] files)
    {
        foreach (var (seriesId, episodeId, path) in files)
        {
            yield return new Deluno.Series.Contracts.SeriesTrackedFileItem(seriesId, episodeId, "lib-1", "Show", 2020, 1, 1, path, 1_000_000, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        }

        await Task.CompletedTask;
    }
}
