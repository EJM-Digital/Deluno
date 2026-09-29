using Deluno.Filesystem;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform;
using Deluno.Series.Data;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// The per-title Rescan folder button (#997).
///
/// <para>Radarr's RescanMovie / Sonarr's RescanSeries walk one title's folder
/// and reconcile the file records with what is on disk. Deluno's rescan does
/// the same question but changes only Deluno's own note about a vanished file;
/// it never deletes, moves or imports anything.</para>
/// </summary>
public sealed class TitleRescanTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("deluno-rescan-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task A_file_that_is_still_there_changes_nothing()
    {
        var file = Touch("Arrival (2016)", "Arrival.mkv");
        var rig = new Rig(this, file);

        var result = await rig.Service.RescanTitleAsync("movies", "movie-1", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("ok", result!.Outcome);
        Assert.Equal(1, result.FilesPresent);
        Assert.Empty(result.Missing);
        rig.Movies.Verify(m => m.MarkTrackedFileMissingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(File.Exists(file));
        Assert.Contains("where Deluno expects it", result.Summary);
    }

    [Fact]
    public async Task A_vanished_file_is_marked_missing_and_told_in_plain_words_and_no_file_is_touched()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "Arrival (2016)")).FullName;
        var gone = Path.Combine(folder, "Arrival.mkv");
        var sibling = Path.Combine(folder, "Arrival.nfo");
        File.WriteAllText(sibling, "x");
        var rig = new Rig(this, gone);

        var result = await rig.Service.RescanTitleAsync("movies", "movie-1", CancellationToken.None);

        Assert.Equal("changed", result!.Outcome);
        Assert.Single(result.Missing);
        Assert.True(result.Missing[0].MarkedMissing);
        rig.Movies.Verify(m => m.MarkTrackedFileMissingAsync("movie-1", "lib-1", gone, It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(File.Exists(sibling));
        Assert.Contains(rig.Activity, a => a.Category == "library.title.rescanned" && a.Message.Contains("No files were touched"));
    }

    [Fact]
    public async Task An_untracked_video_in_the_folder_is_listed_but_never_imported_or_deleted()
    {
        var file = Touch("Arrival (2016)", "Arrival.mkv");
        var extra = Touch("Arrival (2016)", "Arrival.extended.mkv");
        var rig = new Rig(this, file);

        var result = await rig.Service.RescanTitleAsync("movies", "movie-1", CancellationToken.None);

        var found = Assert.Single(result!.Untracked);
        Assert.Equal("Arrival.extended.mkv", found.FileName);
        Assert.False(string.IsNullOrWhiteSpace(found.IssueId));
        Assert.True(File.Exists(extra));
        rig.Movies.Verify(m => m.AddImportRecoveryCaseAsync(It.IsAny<CreateMovieImportRecoveryCaseRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_flat_library_never_lists_every_other_title_as_untracked()
    {
        var file = Path.Combine(_root, "Arrival.mkv");
        File.WriteAllText(file, "x");
        File.WriteAllText(Path.Combine(_root, "Other.mkv"), "x");
        var rig = new Rig(this, file);

        var result = await rig.Service.RescanTitleAsync("movies", "movie-1", CancellationToken.None);

        Assert.Empty(result!.Untracked);
    }

    [Fact]
    public async Task An_unreachable_library_is_never_mistaken_for_deleted_files()
    {
        var gone = Path.Combine(_root, "Arrival (2016)", "Arrival.mkv");
        var rig = new Rig(this, gone, rootOverride: Path.Combine(_root, "not-mounted"));

        var result = await rig.Service.RescanTitleAsync("movies", "movie-1", CancellationToken.None);

        Assert.Equal("library-unreachable", result!.Outcome);
        rig.Movies.Verify(m => m.MarkTrackedFileMissingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_unknown_title_is_not_found()
    {
        var rig = new Rig(this, Path.Combine(_root, "x.mkv"));
        rig.Movies.Setup(m => m.GetByIdAsync("nope", It.IsAny<CancellationToken>())).ReturnsAsync((MovieListItem?)null);

        Assert.Null(await rig.Service.RescanTitleAsync("movies", "nope", CancellationToken.None));
    }

    [Fact]
    public void The_rename_preview_names_the_change_or_says_why_it_would_not_happen()
    {
        var change = RenamePreviewOutcome.Describe("Arrival", "Arrival (2016)", true, false);
        Assert.True(change.WillChange);
        Assert.Equal("will-rename", change.Outcome);
        Assert.Contains("nothing is deleted", change.Message);

        Assert.False(RenamePreviewOutcome.Describe("Arrival (2016)", "arrival (2016)", true, true).WillChange);
        Assert.Equal("name-taken", RenamePreviewOutcome.Describe("A", "B", true, true).Outcome);
        Assert.Equal("no-files", RenamePreviewOutcome.Describe(null, "B", false, false).Outcome);
    }

    private string Touch(string folder, string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, folder)).FullName;
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private sealed class Rig
    {
        public Mock<IMovieCatalogRepository> Movies { get; } = new();
        public List<(string Category, string Message)> Activity { get; } = [];
        public FilesystemReconciliationService Service { get; }

        public Rig(TitleRescanTests owner, string filePath, string? rootOverride = null)
        {
            var now = DateTimeOffset.UnixEpoch;
            var library = new LibraryItem(
                "lib-1", "Films", "movies", "main", rootOverride ?? owner._root, null, null, null, null,
                true, true, "direct", null, null, 0, "block",
                false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
            var libraries = new Mock<ILibrariesRepository>();
            libraries.Setup(l => l.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);

            Movies.Setup(m => m.GetByIdAsync("movie-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MovieListItem(
                    "movie-1", "Arrival", 2016, null, true, true, null, null, null, null, null, null, null, [],
                    null, null, null, null, now, now, FilePath: filePath, LibraryId: "lib-1"));
            Movies.Setup(m => m.MarkTrackedFileMissingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var activity = new Mock<IActivityFeedRepository>();
            activity.Setup(a => a.RecordActivityAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                    It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, string?, string?, string?, string?, CancellationToken>(
                    (category, message, _, _, _, _, _) => Activity.Add((category, message)))
                .ReturnsAsync((Deluno.Jobs.Contracts.ActivityEventItem)null!);

            Service = new FilesystemReconciliationService(
                libraries.Object, Movies.Object, new Mock<ISeriesCatalogRepository>().Object,
                activity.Object, TimeProvider.System);
        }
    }
}
