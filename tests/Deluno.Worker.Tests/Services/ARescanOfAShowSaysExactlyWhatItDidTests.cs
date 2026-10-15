using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// Rescanning a show with several episode files on a real folder: the summary names how many files are there, how many
/// were marked missing, how many could not be, and how many video files in the folder are not tracked, and a rescan
/// only changes Deluno's own note, never a file. Also the unreachable-library note's count, read from whatever its
/// details hold.
/// </summary>
public sealed class ARescanOfAShowSaysExactlyWhatItDidTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("deluno-rescan-show-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string Folder => Directory.CreateDirectory(Path.Combine(root, "Some Show")).FullName;

    private string Episode(string name, bool create)
    {
        var path = Path.Combine(Folder, name);
        if (create) File.WriteAllText(path, "x");
        return path;
    }

    private (FilesystemReconciliationService Service, Mock<ISeriesCatalogRepository> Series) Build(
        IReadOnlyList<(string, string)> episodes,
        Func<string, bool> markable,
        IHealthCheckRepository? health = null)
    {
        var now = DateTimeOffset.UnixEpoch;
        var library = new LibraryItem(
            "lib-1", "Shows", "tv", "main", root, null, null, null, null,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(l => l.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);

        var series = new Mock<ISeriesCatalogRepository>();
        series.Setup(s => s.GetByIdAsync("show-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesListItem(
                "show-1", "Some Show", 2020, null, true, true, null, null, null, null, null, null, null, [],
                null, null, null, null, now, now, LibraryId: "lib-1"));
        series.Setup(s => s.ListEpisodeFilePathsAsync("show-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(episodes.Select(item => (item.Item1, item.Item2)).ToArray());
        series.Setup(s => s.StreamTrackedFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, CancellationToken _) => Array.Empty<SeriesTrackedFileItem>().ToAsyncEnumerable());
        series.Setup(s => s.MarkTrackedFileGoneAsync("show-1", It.IsAny<string?>(), "lib-1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string? _, string _, string path, CancellationToken _) => markable(path));

        var activity = new Mock<IActivityFeedRepository>();
        activity.Setup(a => a.RecordActivityAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Deluno.Jobs.Contracts.ActivityEventItem)null!);

        var service = new FilesystemReconciliationService(
            libraries.Object, new Mock<IMovieCatalogRepository>().Object, series.Object, activity.Object, TimeProvider.System, health);
        return (service, series);
    }

    [Fact]
    public async Task Every_file_there_says_so_with_the_count_and_lists_untracked_videos_without_importing_them()
    {
        var one = Episode("S01E01.mkv", create: true);
        var two = Episode("S01E02.mkv", create: true);
        var strayOne = Episode("S01E03.mkv", create: true);
        var strayTwo = Episode("S01E04.mkv", create: true);
        var (service, series) = Build([("e1", one), ("e2", two)], _ => true);

        var result = await service.RescanTitleAsync("tv", "show-1", CancellationToken.None);

        Assert.Equal("ok", result!.Outcome);
        Assert.Contains("All 2 of its files are where Deluno expects them.", result.Summary);
        Assert.Contains("2 video files in its folder are not tracked; nothing was imported or deleted.", result.Summary);
        Assert.Equal(2, result.Untracked.Count);
        Assert.True(File.Exists(strayOne) && File.Exists(strayTwo));
        series.Verify(s => s.MarkTrackedFileGoneAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Several_files_gone_that_were_all_marked_say_how_many_of_how_many()
    {
        var present = Episode("S01E01.mkv", create: true);
        var (service, _) = Build([("e1", present), ("e2", Episode("S01E02.mkv", false)), ("e3", Episode("S01E03.mkv", false))], _ => true);

        var result = await service.RescanTitleAsync("tv", "show-1", CancellationToken.None);

        Assert.Equal("changed", result!.Outcome);
        Assert.Contains("2 of 3 files are no longer on disk. Deluno marked them missing and stopped wanting them. No files were touched.", result.Summary);
        Assert.True(File.Exists(present));
    }

    [Fact]
    public async Task Several_files_gone_that_could_not_be_marked_say_nothing_was_changed()
    {
        var (service, _) = Build([("e1", Episode("S01E01.mkv", true)), ("e2", Episode("S01E02.mkv", false)), ("e3", Episode("S01E03.mkv", false))], _ => false);

        var result = await service.RescanTitleAsync("tv", "show-1", CancellationToken.None);

        Assert.Equal("not-changed", result!.Outcome);
        Assert.Contains("2 of 3 files are not on disk, but Deluno could not mark them missing, so nothing was changed and the title is still monitored.", result.Summary);
    }

    [Fact]
    public async Task A_mix_of_marked_and_unmarked_files_names_how_many_of_each()
    {
        var markedOne = Episode("S01E02.mkv", false);
        var markedTwo = Episode("S01E03.mkv", false);
        var refused = Episode("S01E04.mkv", false);
        var (service, _) = Build(
            [("e1", Episode("S01E01.mkv", true)), ("e2", markedOne), ("e3", markedTwo), ("e4", refused)],
            path => path != refused);

        var result = await service.RescanTitleAsync("tv", "show-1", CancellationToken.None);

        Assert.Equal("changed", result!.Outcome);
        Assert.Contains("3 of 4 files are not on disk. Deluno marked 2 missing and stopped wanting them; it could not mark the other 1, which are unchanged. No files were touched.", result.Summary);
    }

    [Fact]
    public async Task One_gone_file_in_a_show_with_other_files_present_names_the_count_not_a_single_file()
    {
        var (service, _) = Build([("e1", Episode("S01E01.mkv", true)), ("e2", Episode("S01E02.mkv", false))], _ => true);

        var result = await service.RescanTitleAsync("tv", "show-1", CancellationToken.None);

        Assert.Contains("1 of 2 files are no longer on disk.", result!.Summary);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("{ this is not json", 0)]
    [InlineData("{\"other\":4}", 0)]
    [InlineData("{\"missing\":\"many\"}", 0)]
    [InlineData("{\"missing\":37}", 37)]
    public async Task The_unreachable_notices_missing_count_is_zero_unless_its_details_really_hold_a_number(string? details, int expected)
    {
        var health = new Mock<IHealthCheckRepository>();
        var libraryId = "lib-9";
        health.Setup(h => h.GetAsync(FilesystemReconciliationService.UnreachableNoticePrefix + libraryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthCheckResultItem("n", "library", "Library", HealthCheckStatuses.Problem, "m", null, null, details, DateTimeOffset.UnixEpoch, null, null));
        var (service, _) = Build([], _ => true, health.Object);

        Assert.Equal(expected, await service.GetUnreachableNoticeMissingCountAsync(libraryId, CancellationToken.None));
        Assert.Null(await service.GetUnreachableNoticeMissingCountAsync("lib-without-a-note", CancellationToken.None));
    }
}
