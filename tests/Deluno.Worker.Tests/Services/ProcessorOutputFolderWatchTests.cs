using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Worker.HealthChecks;
using Deluno.Worker.HealthChecks.Checks;
using Deluno.Worker.Services;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>#1514: a missing or unreadable processor output folder is said once in Activity, shown on Health until it is back, and its return is said once.</summary>
public sealed class ProcessorOutputFolderWatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-1514-" + Guid.NewGuid().ToString("n"));
    private readonly List<ActivityEventItem> _feed = [];

    public ProcessorOutputFolderWatchTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private LibraryItem Library(string outputPath, string workflow = "refine-before-import")
    {
        var now = DateTimeOffset.UnixEpoch;
        return new LibraryItem(
            "lib1", "Films", "movies", "main", Path.Combine(_root, "Movies"), null, null, null, null,
            true, true, workflow, null, outputPath, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
    }

    private IActivityFeedRepository Feed()
    {
        var mock = new Mock<IActivityFeedRepository>();
        mock.Setup(feed => feed.ListActivityAsync(It.IsAny<int>(), "library", "lib1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _feed.AsEnumerable().Reverse().ToArray());
        mock.Setup(feed => feed.RecordActivityAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string category, string message, string? details, string? job, string? type, string? id, CancellationToken _) =>
            {
                var item = new ActivityEventItem(Guid.NewGuid().ToString("n"), category, message, details, job, type, id, DateTimeOffset.UtcNow);
                _feed.Add(item);
                return item;
            });
        return mock.Object;
    }

    [Fact]
    public async Task Health_is_asked_to_re_check_the_moment_the_folder_goes_missing_and_the_moment_it_is_back_and_not_on_quiet_scans()
    {
        var folder = Path.Combine(_root, "refined");
        var library = Library(folder);
        var feed = Feed();
        var health = new Mock<IHealthCheckRepository>();

        await ProcessorOutputFolderWatch.RecordChangesAsync([library], feed, CancellationToken.None, health.Object);
        await ProcessorOutputFolderWatch.RecordChangesAsync([library], feed, CancellationToken.None, health.Object);
        health.Verify(h => h.RequestImmediateRunAsync(HealthCheckCategories.Processor, It.IsAny<CancellationToken>()), Times.Once);

        Directory.CreateDirectory(folder);
        await ProcessorOutputFolderWatch.RecordChangesAsync([library], feed, CancellationToken.None, health.Object);
        await ProcessorOutputFolderWatch.RecordChangesAsync([library], feed, CancellationToken.None, health.Object);
        health.Verify(h => h.RequestImmediateRunAsync(HealthCheckCategories.Processor, It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Equal(2, _feed.Count);
    }

    [Fact]
    public async Task A_missing_folder_writes_one_line_naming_the_library_and_folder_however_many_scans_see_it()
    {
        var folder = Path.Combine(_root, "refined");
        var library = Library(folder);
        var feed = Feed();

        var first = await ProcessorOutputFolderWatch.RecordChangesAsync([library], feed, CancellationToken.None);
        await ProcessorOutputFolderWatch.RecordChangesAsync([library], feed, CancellationToken.None);
        await ProcessorOutputFolderWatch.RecordChangesAsync([library], feed, CancellationToken.None);

        var line = Assert.Single(_feed);
        Assert.Equal(ProcessorOutputFolderWatch.MissingCategory, line.Category);
        Assert.Contains("Films", line.Message);
        Assert.Contains(folder, line.Message);
        Assert.Contains("lib1", first);
    }

    [Fact]
    public async Task The_folder_returning_writes_one_line_and_a_later_loss_writes_a_new_one()
    {
        var folder = Path.Combine(_root, "refined");
        var library = Library(folder);
        var feed = Feed();

        await ProcessorOutputFolderWatch.RecordChangesAsync([library], feed, CancellationToken.None);
        Directory.CreateDirectory(folder);
        var back = await ProcessorOutputFolderWatch.RecordChangesAsync([library], feed, CancellationToken.None);
        await ProcessorOutputFolderWatch.RecordChangesAsync([library], feed, CancellationToken.None);

        Assert.Empty(back);
        Assert.Equal(
            [ProcessorOutputFolderWatch.MissingCategory, ProcessorOutputFolderWatch.BackCategory],
            _feed.Select(line => line.Category).ToArray());
        Assert.Contains(folder, _feed[1].Message);

        Directory.Delete(folder);
        await ProcessorOutputFolderWatch.RecordChangesAsync([library], feed, CancellationToken.None);

        Assert.Equal(3, _feed.Count);
        Assert.Equal(ProcessorOutputFolderWatch.MissingCategory, _feed[2].Category);
    }

    [Fact]
    public async Task A_folder_that_was_always_fine_or_a_library_that_does_not_use_a_processor_writes_nothing()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "refined")).FullName;
        var feed = Feed();

        await ProcessorOutputFolderWatch.RecordChangesAsync([Library(folder)], feed, CancellationToken.None);
        var skipped = await ProcessorOutputFolderWatch.RecordChangesAsync(
            [Library(Path.Combine(_root, "never"), workflow: "direct")], feed, CancellationToken.None);

        Assert.Empty(_feed);
        Assert.Empty(skipped);
    }

    [Fact]
    public async Task An_unreadable_folder_is_said_once_as_unreadable_and_its_return_is_said_once()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // permissions are set with chmod; the Windows case is the same code path via UnauthorizedAccessException
        }

        var folder = Directory.CreateDirectory(Path.Combine(_root, "refined")).FullName;
        var before = File.GetUnixFileMode(folder);
        var library = Library(folder);
        var feed = Feed();
        try
        {
            File.SetUnixFileMode(folder, UnixFileMode.None);
            if (ProcessorOutputFolderWatch.Probe(library) != ProcessorOutputFolderState.Unreadable)
            {
                return; // running as root: nothing is unreadable
            }

            await ProcessorOutputFolderWatch.RecordChangesAsync([library], feed, CancellationToken.None);
            await ProcessorOutputFolderWatch.RecordChangesAsync([library], feed, CancellationToken.None);

            var line = Assert.Single(_feed);
            Assert.Contains("cannot read", line.Message);
            Assert.Contains(folder, line.Message);

            File.SetUnixFileMode(folder, before);
            await ProcessorOutputFolderWatch.RecordChangesAsync([library], feed, CancellationToken.None);

            Assert.Equal(ProcessorOutputFolderWatch.BackCategory, _feed[^1].Category);
            Assert.Equal(2, _feed.Count);
        }
        finally
        {
            File.SetUnixFileMode(folder, before);
        }
    }

    [Fact]
    public async Task Health_shows_the_problem_until_the_folder_is_back_then_reads_ok()
    {
        var folder = Path.Combine(_root, "refined");
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Library(folder), Library(Path.Combine(_root, "unused"), workflow: "direct") with { Id = "lib2" }]);
        var check = new ProcessorOutputFolderHealthCheck(libraries.Object);

        var missing = Assert.Single(await check.RunAsync(CancellationToken.None));
        Assert.Equal(HealthCheckStatuses.Problem, missing.Status);
        Assert.Equal("library:lib1:processor-output-folder", missing.Id);
        Assert.Contains(folder, missing.Message);

        Directory.CreateDirectory(folder);
        var ok = Assert.Single(await check.RunAsync(CancellationToken.None));
        Assert.Equal(HealthCheckStatuses.Ok, ok.Status);
    }
}
