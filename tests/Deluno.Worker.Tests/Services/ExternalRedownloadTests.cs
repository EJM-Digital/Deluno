using System.Runtime.CompilerServices;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform;
using Deluno.Platform.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #540: Weir asks Deluno to download a title again, so a track it removed
/// comes back. The file goes to the recycle bin and the title is searched for
/// - and nothing further happens once a step fails.
/// </summary>
public sealed class ExternalRedownloadTests
{
    private const string FilmPath = @"D:\Media\Movies\Blade Runner 2049 (2017)\Blade Runner 2049.mkv";
    private const string EpisodePath = @"D:\Media\TV\Breaking Bad\Season 01\Breaking Bad - S01E01.mkv";

    private readonly Mock<ILibrariesRepository> _libraries = new();
    private readonly Mock<IMovieCatalogRepository> _movies = new();
    private readonly Mock<ISeriesCatalogRepository> _series = new();
    private readonly Mock<IRecycleBinService> _recycleBin = new();
    private readonly Mock<IJobScheduler> _jobs = new();
    private readonly Mock<IActivityFeedRepository> _activity = new();

    public ExternalRedownloadTests()
    {
        var now = DateTimeOffset.UnixEpoch;
        _libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([
            (LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem)) with { Id = "movies-lib", Name = "Movies", MediaType = "movies" },
            (LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem)) with { Id = "tv-lib", Name = "TV", MediaType = "tv" }
        ]);
        _movies.Setup(repo => repo.StreamTrackedFilesAsync("movies-lib", It.IsAny<CancellationToken>()))
            .Returns(() => Items(new MovieTrackedFileItem("movie-1", "movies-lib", "Blade Runner 2049", 2017, FilmPath, 1000, now, now)));
        _movies.Setup(repo => repo.StreamTrackedFilesAsync("tv-lib", It.IsAny<CancellationToken>())).Returns(() => Items<MovieTrackedFileItem>());
        _series.Setup(repo => repo.StreamTrackedFilesAsync("tv-lib", It.IsAny<CancellationToken>()))
            .Returns(() => Items(new SeriesTrackedFileItem("series-1", "episode-1", "tv-lib", "Breaking Bad", 2008, 1, 1, EpisodePath, 1000, now, now)));
        _movies.Setup(repo => repo.MarkTrackedFileMissingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _series.Setup(repo => repo.MarkTrackedFileMissingAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _recycleBin.Setup(bin => bin.MoveAsync(It.IsAny<IReadOnlyList<TrackedLibraryFile>>(), It.IsAny<IReadOnlyList<LibraryItem>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecycleBinMoveResult(1, 0, [], []));
        _jobs.Setup(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobQueueItem)RuntimeHelpers.GetUninitializedObject(typeof(JobQueueItem)) with { Id = "search-1" });
    }

    [Fact]
    public async Task A_film_goes_to_the_recycle_bin_is_marked_missing_and_is_searched_for()
    {
        EnqueueJobRequest? search = null;
        _jobs.Setup(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EnqueueJobRequest, CancellationToken>((request, _) => search = request)
            .ReturnsAsync((JobQueueItem)RuntimeHelpers.GetUninitializedObject(typeof(JobQueueItem)) with { Id = "search-1" });

        var outcome = await Service().RedownloadAsync(new ExternalRedownloadRequest(FilmPath, "Keep Japanese audio (removed earlier by Weir).", "Weir"), CancellationToken.None);

        var done = Assert.IsType<ExternalRedownloadService.Done>(outcome);
        Assert.Equal(new ExternalRedownloadResult("movie-1", "movie", "search-1", true, done.Result.Message), done.Result);
        Assert.Equal("Weir asked Deluno to download Blade Runner 2049 again: Keep Japanese audio (removed earlier by Weir). The file is in the recycle bin and a search has started.", done.Result.Message);
        _recycleBin.Verify(bin => bin.MoveAsync(It.Is<IReadOnlyList<TrackedLibraryFile>>(files => files.Single().FilePath == FilmPath && files.Single().LibraryId == "movies-lib"), It.IsAny<IReadOnlyList<LibraryItem>>(), It.IsAny<CancellationToken>()), Times.Once);
        _movies.Verify(repo => repo.MarkTrackedFileMissingAsync("movie-1", "movies-lib", FilmPath, It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(search);
        Assert.Equal(LibrarySearchJobTypes.Movies, search!.JobType);
        using var payload = JsonDocument.Parse(search.PayloadJson!);
        Assert.Equal("movie-1", payload.RootElement.GetProperty("targetEntityId").GetString());
        Assert.Equal("movies-lib", payload.RootElement.GetProperty("libraryId").GetString());
        _activity.Verify(feed => feed.RecordActivityAsync("external.redownload", done.Result.Message, It.IsAny<string?>(), "search-1", "movie", "movie-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_film_the_bin_took_but_that_could_not_be_marked_missing_is_logged_and_still_searched_for()
    {
        _movies.Setup(repo => repo.MarkTrackedFileMissingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var logger = new Mock<ILogger<ExternalRedownloadService>>();
        logger.Setup(log => log.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var outcome = await Service(null, logger.Object).RedownloadAsync(new ExternalRedownloadRequest(FilmPath, null, "Weir"), CancellationToken.None);

        var done = Assert.IsType<ExternalRedownloadService.Done>(outcome);
        Assert.Equal("search-1", done.Result.SearchJobId);
        _jobs.Verify(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        logger.Verify(log => log.Log(
            LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    /// <summary>Seen on the rig: the film was alone in its folder, the bin took the folder, and "0 files moved" was read as a failure.</summary>
    [Fact]
    public async Task A_film_moved_to_the_bin_with_its_folder_counts_as_removed()
    {
        _recycleBin.Setup(bin => bin.MoveAsync(It.IsAny<IReadOnlyList<TrackedLibraryFile>>(), It.IsAny<IReadOnlyList<LibraryItem>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecycleBinMoveResult(0, 1, [], []));

        var outcome = await Service().RedownloadAsync(new ExternalRedownloadRequest(FilmPath, null, "Weir"), CancellationToken.None);

        Assert.IsType<ExternalRedownloadService.Done>(outcome);
        _movies.Verify(repo => repo.MarkTrackedFileMissingAsync("movie-1", "movies-lib", FilmPath, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_episode_is_marked_missing_on_its_show_and_the_show_is_searched()
    {
        var outcome = await Service().RedownloadAsync(new ExternalRedownloadRequest(EpisodePath, null, "Weir"), CancellationToken.None);

        var done = Assert.IsType<ExternalRedownloadService.Done>(outcome);
        Assert.Equal("series", done.Result.MediaKind);
        Assert.Equal("series-1", done.Result.TitleId);
        Assert.StartsWith("Weir asked Deluno to download Breaking Bad S01E01 again.", done.Result.Message, StringComparison.Ordinal);
        _series.Verify(repo => repo.MarkTrackedFileMissingAsync("series-1", "episode-1", "tv-lib", EpisodePath, It.IsAny<CancellationToken>()), Times.Once);
        _jobs.Verify(scheduler => scheduler.EnqueueAsync(It.Is<EnqueueJobRequest>(request => request.JobType == LibrarySearchJobTypes.For("tv")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_path_Deluno_does_not_track_changes_nothing()
    {
        var outcome = await Service().RedownloadAsync(new ExternalRedownloadRequest(@"D:\Media\Movies\Unknown.mkv", null, "Weir"), CancellationToken.None);

        Assert.IsType<ExternalRedownloadService.NotTracked>(outcome);
        _recycleBin.Verify(bin => bin.MoveAsync(It.IsAny<IReadOnlyList<TrackedLibraryFile>>(), It.IsAny<IReadOnlyList<LibraryItem>>(), It.IsAny<CancellationToken>()), Times.Never);
        _jobs.Verify(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A file that stays in the library is not searched for: the search would find the library already holds it.</summary>
    [Fact]
    public async Task A_file_that_would_not_move_is_left_and_nothing_is_searched()
    {
        _recycleBin.Setup(bin => bin.MoveAsync(It.IsAny<IReadOnlyList<TrackedLibraryFile>>(), It.IsAny<IReadOnlyList<LibraryItem>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecycleBinMoveResult(0, 0, [], ["The file is in use."]));

        var outcome = await Service().RedownloadAsync(new ExternalRedownloadRequest(FilmPath, null, "Weir"), CancellationToken.None);

        var refused = Assert.IsType<ExternalRedownloadService.NotRemoved>(outcome);
        Assert.Contains("The file is in use.", refused.Message, StringComparison.Ordinal);
        _movies.Verify(repo => repo.MarkTrackedFileMissingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _jobs.Verify(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_failure_while_clearing_what_blocks_the_download_is_logged_and_the_search_still_runs()
    {
        var failing = new Mock<IDownloadDispatchesRepository>();
        failing.Setup(repo => repo.QueryDispatchesAsync(It.IsAny<DispatchQueryFilter>(), It.IsAny<DispatchPaginationOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the dispatch store is unavailable"));
        var logger = new Mock<ILogger<ExternalRedownloadService>>();
        logger.Setup(log => log.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var outcome = await Service(failing.Object, logger.Object).RedownloadAsync(new ExternalRedownloadRequest(FilmPath, null, "Weir"), CancellationToken.None);

        var done = Assert.IsType<ExternalRedownloadService.Done>(outcome);
        Assert.Equal("search-1", done.Result.SearchJobId);
        _jobs.Verify(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        logger.Verify(log => log.Log(
            LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<InvalidOperationException>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task A_search_that_cannot_be_queued_leaves_the_file_removed_and_says_to_search_from_the_title_page()
    {
        _jobs.Setup(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the job queue is unavailable"));

        var outcome = await Service().RedownloadAsync(new ExternalRedownloadRequest(FilmPath, null, "Weir"), CancellationToken.None);

        var done = Assert.IsType<ExternalRedownloadService.Done>(outcome);
        Assert.Null(done.Result.SearchJobId);
        Assert.True(done.Result.FileRemoved);
        Assert.Contains("the search could not be queued; search for it from its page", done.Result.Message, StringComparison.Ordinal);
        _movies.Verify(repo => repo.MarkTrackedFileMissingAsync("movie-1", "movies-lib", FilmPath, It.IsAny<CancellationToken>()), Times.Once);
    }

    private ExternalRedownloadService Service(IDownloadDispatchesRepository? dispatchesOverride = null, ILogger<ExternalRedownloadService>? logger = null)
    {
        var dispatches = new Mock<IDownloadDispatchesRepository>();
        dispatches.Setup(repo => repo.QueryDispatchesAsync(It.IsAny<DispatchQueryFilter>(), It.IsAny<DispatchPaginationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Array.Empty<DownloadDispatchItem>(), (string?)null));
        var exclusions = new Mock<IUnifiedExclusionRepository>();
        exclusions.Setup(repo => repo.ListActiveAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var blocked = new Mock<IBlockedReleaseRepository>();
        blocked.Setup(repo => repo.ListForAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var processors = Mock.Of<IProcessorRepository>();
        var overrides = new AcquisitionOverrideService(processors, exclusions.Object, Mock.Of<IDownloadClientTelemetryService>(), NullLogger<AcquisitionOverrideService>.Instance, dispatchesOverride ?? dispatches.Object, blocked.Object);
        return new ExternalRedownloadService(
            _libraries.Object, _movies.Object, _series.Object, _recycleBin.Object, overrides,
            dispatchesOverride ?? dispatches.Object, processors, exclusions.Object, blocked.Object, _jobs.Object, _activity.Object,
            logger ?? NullLogger<ExternalRedownloadService>.Instance);
    }

    private static async IAsyncEnumerable<T> Items<T>(params T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
        }

        await Task.CompletedTask;
    }
}
