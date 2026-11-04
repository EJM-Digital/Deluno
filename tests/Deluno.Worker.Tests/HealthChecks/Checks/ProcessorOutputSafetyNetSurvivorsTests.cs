using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Data;
using Deluno.Worker.HealthChecks.Checks;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>Rules about several files in one folder, and the words of the second-copy row, that the first round left loose.</summary>
public sealed class ProcessorOutputSafetyNetSurvivorsTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");
    private readonly string _root = Directory.CreateTempSubdirectory("deluno-safetynet2-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string File_(string relative, bool ready = true)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "content");
        File.SetLastWriteTimeUtc(path, ready ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow);
        return path;
    }

    private async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(string mediaType = "movies", JobQueueItem[]? jobs = null, bool holdAlien = false)
    {
        var library = new LibraryItem(
            "library-1", "Films", mediaType, "main", "/library", null, null, null, null,
            true, true, "refine-before-import", "Weir", _root, 360, "block",
            true, true, true, 6, 6, 10, null, null, "active", false, null, null, Now, Now);
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(r => r.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);
        var processors = new Mock<IProcessorRepository>();
        processors.Setup(r => r.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var queue = new Mock<IJobQueueRepository>();
        queue.Setup(r => r.ListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(jobs ?? []);
        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(r => r.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(holdAlien
            ? [new MovieListItem("m1", "Alien", 1979, null, true, true, null, null, null, null, null, null, null, [], null, null, null, null, Now, Now)]
            : []);
        return await new ProcessorOutputSafetyNetHealthCheck(libraries.Object, processors.Object, queue.Object, movies.Object).RunAsync(CancellationToken.None);
    }

    private static JobQueueItem Import(string source, string status)
        => TestJobs.Create("filesystem.import.execute", payloadJson: "{\"preview\":{\"sourcePath\":\"" + source.Replace("\\", "\\\\") + "\"}}", status: status);

    [Fact]
    public async Task A_folder_with_one_file_still_being_written_is_not_listed_even_when_another_is_ready()
    {
        File_("Show.S01/Show.S01E01.mkv");
        File_("Show.S01/Show.S01E02.mkv", ready: false);

        Assert.Empty(await RunAsync("tv"));
    }

    [Fact]
    public async Task A_folder_of_a_video_and_a_scrap_is_media()
    {
        File_("Show.S01/Show.S01E01.mkv");
        File_("Show.S01/readme.nfo");

        var outcome = Assert.Single(await RunAsync("tv"));

        Assert.Contains("\"isMedia\":true", outcome.DetailsJson);
    }

    [Fact]
    public async Task A_second_copy_folder_with_one_file_still_being_written_is_not_listed()
    {
        File_("Alien.1979.1080p/Alien.1979.1080p.mkv");
        File_("Alien.1979.1080p/Alien.1979.1080p.sub.mkv", ready: false);

        Assert.Empty(await RunAsync(holdAlien: true));
    }

    [Fact]
    public async Task A_second_copy_folder_is_kept_out_by_any_one_running_import_of_its_files_and_not_by_a_finished_one()
    {
        var first = File_("Alien.1979.1080p/Alien.1979.1080p.mkv");
        File_("Alien.1979.1080p/Alien.1979.1080p.b.mkv");

        Assert.Empty(await RunAsync(jobs: [Import(first, "running")], holdAlien: true));
        Assert.Single(await RunAsync(jobs: [Import(first, "completed")], holdAlien: true));
    }

    [Fact]
    public async Task The_second_copy_row_is_named_for_the_folder_in_lower_case_and_says_it_is_media()
    {
        File_("Alien.1979.1080p/Alien.1979.1080p.mkv");

        var outcome = Assert.Single(await RunAsync(holdAlien: true));

        Assert.Equal("processor-output:library-1:alien.1979.1080p", outcome.Id);
        Assert.Contains("\"isMedia\":true", outcome.DetailsJson);
        Assert.Contains("\"kind\":\"folder\"", outcome.DetailsJson);
    }

    [Fact]
    public async Task A_folder_named_only_with_the_title_and_year_is_still_a_second_copy()
    {
        File_("Alien.1979/Alien.1979.mkv");

        var outcome = Assert.Single(await RunAsync(holdAlien: true));

        Assert.Contains("second copy", outcome.Message);
    }
}
