using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// One missed sighting must never unmonitor a title (#1065, #1070). A drive
/// that drops out part-way through a check makes every file behind it look
/// deleted; the check re-looks right before each write and treats a large share
/// missing in one pass as an unreachable library, writing nothing.
/// </summary>
public sealed class LibraryDropoutTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("deluno-dropout-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_large_share_missing_in_one_pass_is_an_unreachable_library_and_nothing_is_written()
    {
        var rig = new Rig(this, present: 10, missing: 30);

        var report = await rig.Service.ScanAsync(CancellationToken.None);

        Assert.DoesNotContain(report.Issues, i => i.Kind == "missingTrackedFile");
        var unreachable = Assert.Single(report.Issues, i => i.Kind == "libraryRootUnreachable");
        Assert.Equal("lib-1", unreachable.LibraryId);
        var notice = Assert.Single(rig.HealthOutcomes);
        Assert.Equal(HealthCheckCategories.Notice, notice.Category);
        Assert.Equal(HealthCheckStatuses.Problem, notice.Status);
        Assert.Equal(
            "Films looks unreachable: 30 of 40 files could not be found. Nothing was changed.",
            notice.Message);
        Assert.Single(rig.Activity, a => a.Category == "library.unreachable");
        rig.Movies.Verify(m => m.MarkTrackedFileGoneAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_notice_is_raised_once_while_it_is_still_on_screen()
    {
        var rig = new Rig(this, present: 10, missing: 30);

        await rig.Service.ScanAsync(CancellationToken.None);
        rig.ExistingNotice = true;
        await rig.Service.ScanAsync(CancellationToken.None);

        Assert.Single(rig.HealthOutcomes);
        Assert.Single(rig.Activity, a => a.Category == "library.unreachable");
    }

    [Fact]
    public async Task A_few_deleted_files_are_still_reported_one_by_one()
    {
        var rig = new Rig(this, present: 38, missing: 2);

        var report = await rig.Service.ScanAsync(CancellationToken.None);

        Assert.Equal(2, report.Issues.Count(i => i.Kind == "missingTrackedFile"));
        Assert.DoesNotContain(report.Issues, i => i.Kind == "libraryRootUnreachable");
        Assert.Empty(rig.HealthOutcomes);
    }

    [Theory]
    [InlineData(19, 40, false)]
    [InlineData(20, 40, true)]
    [InlineData(20, 100, false)]
    [InlineData(25, 100, true)]
    [InlineData(0, 0, false)]
    public void The_dropout_rule_needs_twenty_files_and_a_quarter_of_the_library(int missing, int total, bool expected)
        => Assert.Equal(expected, FilesystemReconciliationService.LooksLikeDropout(missing, total));

    [Fact]
    public async Task A_root_that_disappears_mid_scan_writes_nothing()
    {
        var rig = new Rig(this, present: 38, missing: 2);
        var report = await rig.Service.ScanAsync(CancellationToken.None);
        var issue = report.Issues.First(i => i.Kind == "missingTrackedFile");

        // The drive goes away between the scan and the repair.
        Directory.Delete(_root, recursive: true);
        var result = await rig.Service.RepairAsync(
            new FilesystemReconciliationRepairRequest(issue.Id, "mark-missing"), CancellationToken.None);

        Assert.False(result.Repaired);
        Assert.True(result.LibraryUnreachable);
        rig.Movies.Verify(m => m.MarkTrackedFileGoneAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(rig.HealthOutcomes, o => o.Status == HealthCheckStatuses.Problem);
    }

    [Fact]
    public async Task A_root_that_becomes_an_empty_mount_mid_scan_writes_nothing()
    {
        var rig = new Rig(this, present: 38, missing: 2);
        var report = await rig.Service.ScanAsync(CancellationToken.None);
        var issue = report.Issues.First(i => i.Kind == "missingTrackedFile");

        foreach (var entry in Directory.EnumerateFileSystemEntries(_root))
        {
            Directory.Delete(entry, recursive: true);
        }

        var result = await rig.Service.RepairAsync(
            new FilesystemReconciliationRepairRequest(issue.Id, "mark-missing"), CancellationToken.None);

        Assert.True(result.LibraryUnreachable);
        rig.Movies.Verify(m => m.MarkTrackedFileGoneAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_file_that_came_back_before_the_write_is_left_alone()
    {
        var rig = new Rig(this, present: 38, missing: 2);
        var report = await rig.Service.ScanAsync(CancellationToken.None);
        var issue = report.Issues.First(i => i.Kind == "missingTrackedFile");
        Directory.CreateDirectory(Path.GetDirectoryName(issue.Path)!);
        File.WriteAllText(issue.Path, "x");

        var result = await rig.Service.RepairAsync(
            new FilesystemReconciliationRepairRequest(issue.Id, "mark-missing"), CancellationToken.None);

        Assert.False(result.Repaired);
        Assert.False(result.LibraryUnreachable);
        rig.Movies.Verify(m => m.MarkTrackedFileGoneAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_single_real_delete_still_unmonitors_the_title()
    {
        var rig = new Rig(this, present: 38, missing: 1);
        var report = await rig.Service.ScanAsync(CancellationToken.None);
        var issue = Assert.Single(report.Issues, i => i.Kind == "missingTrackedFile");

        var result = await rig.Service.RepairAsync(
            new FilesystemReconciliationRepairRequest(issue.Id, "mark-missing"), CancellationToken.None);

        Assert.True(result.Repaired);
        rig.Movies.Verify(m => m.MarkTrackedFileGoneAsync(It.IsAny<string>(), "lib-1", issue.Path, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_title_rescan_in_a_library_that_mostly_went_missing_changes_nothing()
    {
        var rig = new Rig(this, present: 10, missing: 30);

        var result = await rig.Service.RescanTitleAsync("movies", "movie-0", CancellationToken.None);

        // movie-0 is one of the missing ones
        Assert.NotNull(result);
        Assert.Equal("library-unreachable", result!.Outcome);
        rig.Movies.Verify(m => m.MarkTrackedFileGoneAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("still monitored", result.Summary);
    }

    [Fact]
    public async Task A_title_rescan_of_one_real_delete_still_unmonitors_it()
    {
        var rig = new Rig(this, present: 38, missing: 1);

        var result = await rig.Service.RescanTitleAsync("movies", "movie-0", CancellationToken.None);

        Assert.Equal("changed", result!.Outcome);
        rig.Movies.Verify(m => m.MarkTrackedFileGoneAsync("movie-0", "lib-1", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_scheduled_check_stops_writing_to_a_library_once_it_looks_unreachable()
    {
        var issues = Enumerable.Range(0, 3)
            .Select(i => new FilesystemReconciliationIssue(
                $"id-{i}", "missingTrackedFile", "critical", "movies", "lib-1", "Films", $"/x/{i}.mkv", $"Film {i}",
                "s", "a", ["mark-missing"]))
            .ToArray();
        var reconciliation = new Mock<IFilesystemReconciliationService>();
        reconciliation.Setup(r => r.ScanAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilesystemReconciliationReport(DateTimeOffset.UnixEpoch, 1, 3, issues));
        reconciliation.Setup(r => r.RepairAsync(It.IsAny<FilesystemReconciliationRepairRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilesystemReconciliationRepairResult(false, "mark-missing", "unreachable", LibraryUnreachable: true));

        var result = await new LibraryFileCheckService(
                reconciliation.Object, Mock.Of<IActivityFeedRepository>(), NullLogger<LibraryFileCheckService>.Instance)
            .RunAsync(CancellationToken.None);

        reconciliation.Verify(r => r.RepairAsync(It.IsAny<FilesystemReconciliationRepairRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(0, result.MarkedMissing);
        Assert.Equal(1, result.UnreachableRoots);
    }

    private sealed class Rig
    {
        public Mock<IMovieCatalogRepository> Movies { get; } = new();
        public List<(string Category, string Message)> Activity { get; } = [];
        public List<HealthCheckOutcome> HealthOutcomes { get; } = [];
        public bool ExistingNotice { get; set; }
        public FilesystemReconciliationService Service { get; }

        public Rig(LibraryDropoutTests owner, int present, int missing)
        {
            var now = DateTimeOffset.UnixEpoch;
            var library = new LibraryItem(
                "lib-1", "Films", "movies", "main", owner._root, null, null, null, null,
                true, true, "direct", null, null, 0, "block",
                false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
            var libraries = new Mock<ILibrariesRepository>();
            libraries.Setup(l => l.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);

            // The first `missing` movies have no file on disk; the rest do.
            var tracked = new List<MovieTrackedFileItem>();
            for (var i = 0; i < missing + present; i++)
            {
                var folder = Path.Combine(owner._root, $"Film {i}");
                var path = Path.Combine(folder, $"Film {i}.mkv");
                if (i >= missing)
                {
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(path, "x");
                }
                else
                {
                    Directory.CreateDirectory(owner._root);
                }

                tracked.Add(new MovieTrackedFileItem($"movie-{i}", "lib-1", $"Film {i}", 2020, path, 1, null, null));
            }

            Movies.Setup(m => m.StreamTrackedFilesAsync("lib-1", It.IsAny<CancellationToken>()))
                .Returns((string _, CancellationToken _) => tracked.ToAsyncEnumerable());
            Movies.Setup(m => m.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string id, CancellationToken _) =>
                {
                    var item = tracked.First(t => t.MovieId == id);
                    return Task.FromResult<MovieListItem?>(new MovieListItem(
                        id, item.Title, 2020, null, true, true, null, null, null, null, null, null, null, [],
                        null, null, null, null, now, now, FilePath: item.FilePath, LibraryId: "lib-1"));
                });
            Movies.Setup(m => m.MarkTrackedFileGoneAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var activity = new Mock<IActivityFeedRepository>();
            activity.Setup(a => a.RecordActivityAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                    It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, string?, string?, string?, string?, CancellationToken>(
                    (category, message, _, _, _, _, _) => Activity.Add((category, message)))
                .ReturnsAsync((Deluno.Jobs.Contracts.ActivityEventItem)null!);

            var health = new Mock<IHealthCheckRepository>();
            health.Setup(h => h.UpsertResultsAsync(It.IsAny<IReadOnlyList<HealthCheckOutcome>>(), It.IsAny<CancellationToken>()))
                .Callback<IReadOnlyList<HealthCheckOutcome>, CancellationToken>((outcomes, _) => HealthOutcomes.AddRange(outcomes))
                .ReturnsAsync([]);
            health.Setup(h => h.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string id, CancellationToken _) => Task.FromResult<HealthCheckResultItem?>(
                    ExistingNotice
                        ? new HealthCheckResultItem(id, HealthCheckCategories.Notice, "n", HealthCheckStatuses.Problem, "m", null, null, null, now, now, null)
                        : null));

            Service = new FilesystemReconciliationService(
                libraries.Object, Movies.Object, new Mock<ISeriesCatalogRepository>().Object,
                activity.Object, TimeProvider.System, health.Object);
        }
    }
}
