using Deluno.Jobs.Contracts;
using Deluno.Movies.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>#819, #926: the cases the first set leaves out: what resolves to nothing, and what is read once and then remembered.</summary>
public sealed class SharingTrackedFileCacheMoreTests
{
    private static DispatchCatalogueLink Dispatch(string entityType, string entityId, string libraryId = "lib-1")
        => new("dispatch-1", entityType, entityId, LibraryId: libraryId);

    private static async IAsyncEnumerable<SeriesTrackedFileItem> Files(params (string SeriesId, string EpisodeId, string Path)[] files)
    {
        foreach (var (seriesId, episodeId, path) in files)
        {
            yield return new SeriesTrackedFileItem(seriesId, episodeId, "lib-1", "Show", 2020, 1, 1, path, 1_000_000, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        }

        await Task.CompletedTask;
    }

    private static (SharingTrackedFileCache Cache, Mock<ISeriesCatalogRepository> Series) Build(params (string SeriesId, string EpisodeId, string Path)[] files)
    {
        var series = new Mock<ISeriesCatalogRepository>();
        series.Setup(repo => repo.StreamTrackedFilesAsync("lib-1", It.IsAny<CancellationToken>())).Returns(() => Files(files));
        return (new SharingTrackedFileCache(new Mock<IMovieCatalogRepository>(MockBehavior.Strict).Object, series.Object), series);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_dispatch_with_no_title_resolves_nothing(string entityId)
    {
        var (cache, _) = Build();

        Assert.Null(await cache.ResolveAsync(Dispatch("movie", entityId), "lib-1", CancellationToken.None));
        Assert.Null(await cache.ResolveAsync(Dispatch("movie", "m1"), "  ", CancellationToken.None));
    }

    [Fact]
    public async Task A_movie_the_library_has_no_file_for_resolves_nothing()
    {
        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repo => repo.StreamTrackedFilesAsync("lib-1", It.IsAny<CancellationToken>())).Returns(MovieFiles());
        var cache = new SharingTrackedFileCache(movies.Object, new Mock<ISeriesCatalogRepository>(MockBehavior.Strict).Object);

        Assert.Null(await cache.ResolveAsync(Dispatch("movie", "m9"), "lib-1", CancellationToken.None));
    }

    private static async IAsyncEnumerable<Deluno.Movies.Contracts.MovieTrackedFileItem> MovieFiles()
    {
        await Task.CompletedTask;
        yield break;
    }

    [Fact]
    public async Task A_kind_of_title_that_is_neither_a_movie_nor_an_episode_nor_a_show_resolves_nothing()
    {
        var (cache, series) = Build(("s1", "e1", "/lib/tv/a.mkv"));

        Assert.Null(await cache.ResolveAsync(Dispatch("album", "a1"), "lib-1", CancellationToken.None));
        series.Verify(repo => repo.StreamTrackedFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_episode_deluno_cannot_place_in_any_series_resolves_nothing()
    {
        var (cache, series) = Build(("s1", "e1", "/lib/tv/a.mkv"));
        series.Setup(repo => repo.GetEpisodeSeriesIdAsync("lost", It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        Assert.Null(await cache.ResolveAsync(Dispatch("episode", "lost"), "lib-1", CancellationToken.None));
    }

    [Fact]
    public async Task A_show_with_no_file_in_this_library_resolves_nothing()
    {
        var (cache, _) = Build(("s1", "e1", "/lib/tv/a.mkv"));

        Assert.Null(await cache.ResolveAsync(Dispatch("series", "s2"), "lib-1", CancellationToken.None));
    }

    [Fact]
    public async Task Episodes_and_show_files_are_read_once_for_all_the_lookups_in_a_library()
    {
        var (cache, series) = Build(("s1", "e1", "/lib/tv/a.mkv"), ("s1", "e2", "/lib/tv/b.mkv"));

        Assert.Equal("/lib/tv/a.mkv", await cache.ResolveAsync(Dispatch("episode", "e1"), "lib-1", CancellationToken.None));
        Assert.Equal("/lib/tv/b.mkv", await cache.ResolveAsync(Dispatch("episode", "e2"), "lib-1", CancellationToken.None));
        Assert.Equal("/lib/tv/a.mkv", await cache.ResolveAsync(Dispatch("series", "s1"), "lib-1", CancellationToken.None));
        Assert.Equal("/lib/tv/a.mkv", await cache.ResolveAsync(Dispatch("series", "s1"), "lib-1", CancellationToken.None));

        series.Verify(repo => repo.StreamTrackedFilesAsync("lib-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Show_files_asked_first_also_fill_the_episode_lookup_without_a_second_read()
    {
        var (cache, series) = Build(("s1", "e1", "/lib/tv/a.mkv"));

        Assert.Equal("/lib/tv/a.mkv", await cache.ResolveAsync(Dispatch("series", "s1"), "lib-1", CancellationToken.None));
        Assert.Equal("/lib/tv/a.mkv", await cache.ResolveAsync(Dispatch("episode", "e1"), "lib-1", CancellationToken.None));

        series.Verify(repo => repo.StreamTrackedFilesAsync("lib-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_file_with_no_episode_is_kept_for_its_show_only()
    {
        var (cache, _) = Build(("s1", "", "/lib/tv/extras.mkv"));

        Assert.Equal("/lib/tv/extras.mkv", await cache.ResolveAsync(Dispatch("series", "s1"), "lib-1", CancellationToken.None));
    }
}
