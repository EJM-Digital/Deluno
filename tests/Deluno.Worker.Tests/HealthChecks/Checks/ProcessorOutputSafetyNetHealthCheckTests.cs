using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.HealthChecks.Checks;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// #921: a safety net, not a routine feature. Deluno starts every download and
/// every import, so in normal running nothing should ever be left in a
/// library's processor output folder that no hand-off is waiting for.
/// </summary>
public sealed class ProcessorOutputSafetyNetHealthCheckTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("deluno-processor-output-").FullName;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task A_stray_video_file_is_reported_as_media()
    {
        var file = WriteReadyFile(Path.Combine(_root, "Sintel.2010.1080p.mkv"));

        var outcome = Assert.Single(await RunAsync());

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Contains("Sintel.2010.1080p.mkv", outcome.Message);
        Assert.Contains("path\":\"" + JsonPath(file), outcome.DetailsJson);
        Assert.Contains("\"isMedia\":true", outcome.DetailsJson);
    }

    [Fact]
    public async Task A_stray_season_folder_is_reported_as_media()
    {
        var season = Directory.CreateDirectory(Path.Combine(_root, "Lioness.S02")).FullName;
        WriteReadyFile(Path.Combine(season, "Lioness.S02E01.mkv"));
        WriteReadyFile(Path.Combine(season, "Lioness.S02E02.mkv"));

        var outcome = Assert.Single(await RunAsync());

        Assert.Contains("season folder", outcome.Message);
        Assert.Contains("Lioness.S02", outcome.Message);
        // A library named Movies is not "Movies's": the sentence names the library, then owns the folder.
        Assert.StartsWith("The Movies library's processor output folder", outcome.Message);
        Assert.DoesNotContain("Movies's", outcome.Message);
        Assert.Contains("\"kind\":\"folder\"", outcome.DetailsJson);
    }

    [Fact]
    public async Task Non_video_scraps_are_reported_but_not_offered_import()
    {
        WriteReadyFile(Path.Combine(_root, "release.nfo"));

        var outcome = Assert.Single(await RunAsync());

        Assert.Contains("not media", outcome.Message);
        Assert.Contains("\"isMedia\":false", outcome.DetailsJson);
    }

    [Fact]
    public async Task Files_belonging_to_an_active_hand_off_are_never_listed()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Sintel.2010.1080p"));
        WriteReadyFile(Path.Combine(_root, "Sintel.2010.1080p", "Sintel.2010.1080p.mkv"));

        var outcomes = await RunAsync(handoffs:
        [
            Handoff(sourcePath: "/downloads/Sintel.2010.1080p")
        ]);

        Assert.Empty(outcomes);
    }

    [Fact]
    public async Task A_file_still_being_written_is_never_listed()
    {
        // No SetLastWriteTimeUtc: a file just created is inside the stable window.
        File.WriteAllText(Path.Combine(_root, "still.copying.mkv"), "partial");

        Assert.Empty(await RunAsync());
    }

    [Fact]
    public async Task A_file_already_queued_for_import_is_never_listed()
    {
        var file = WriteReadyFile(Path.Combine(_root, "Sintel.2010.1080p.mkv"));
        var job = TestJobs.Create(
            "filesystem.import.execute",
            payloadJson: "{\"preview\":{\"sourcePath\":\"" + JsonPath(file) + "\"}}");

        Assert.Empty(await RunAsync(jobs: [job]));
    }

    [Fact]
    public async Task An_empty_folder_is_left_alone()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Empty.Folder"));

        Assert.Empty(await RunAsync());
    }

    [Fact]
    public async Task A_second_copy_of_a_film_the_library_holds_is_said_so_even_when_an_import_for_it_finished()
    {
        var spare = WriteReadyFile(Path.Combine(_root, "Pulp.Fiction.1994.1080p.mkv"));
        var finished = TestJobs.Create(
            "filesystem.import.execute",
            payloadJson: "{\"preview\":{\"sourcePath\":\"" + JsonPath(spare) + "\"}}",
            status: "completed");

        var outcome = Assert.Single(await RunAsync(jobs: [finished], films: [Film("Pulp Fiction", 1994, hasFile: true)]));

        Assert.Contains("second copy", outcome.Message);
        Assert.Contains("Pulp Fiction (1994)", outcome.Message);
        Assert.Contains("\"spareCopyOf\":\"Pulp Fiction (1994)\"", outcome.DetailsJson);
    }

    [Fact]
    public async Task A_film_the_library_does_not_hold_is_not_called_a_second_copy()
    {
        WriteReadyFile(Path.Combine(_root, "Pulp.Fiction.1994.1080p.mkv"));

        var outcome = Assert.Single(await RunAsync(films: [Film("Pulp Fiction", 1994, hasFile: false)]));

        Assert.DoesNotContain("second copy", outcome.Message);
        Assert.DoesNotContain("spareCopyOf", outcome.DetailsJson);
    }

    private static MovieListItem Film(string title, int year, bool hasFile)
        => new("movie-" + title, title, year, null, true, hasFile, null, null, null, null, null, null, null, [], null, null, null, null, Now, Now);

    private static string JsonPath(string path) => path.Replace("\\", "\\\\");

    private static string WriteReadyFile(string path)
    {
        File.WriteAllText(path, "content");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        return path;
    }

    private ProcessorHandoffItem Handoff(string sourcePath) => new(
        "handoff-1", "library-1", "movie", "client-1", "queue-1", "Sintel.2010.1080p", sourcePath,
        "Weir", ProcessorHandoffStatuses.Waiting, null, null, null, Now.AddHours(-1), Now.AddMinutes(-5));

    private async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(
        IReadOnlyList<ProcessorHandoffItem>? handoffs = null,
        IReadOnlyList<Deluno.Jobs.Contracts.JobQueueItem>? jobs = null,
        IReadOnlyList<MovieListItem>? films = null)
    {
        var library = new LibraryItem(
            "library-1", "Movies", "movies", "main", "/library", null, null, null, null,
            true, true, "refine-before-import", "Weir", _root, 360, "block",
            true, true, true, 6, 6, 10, null, null, "active", false, null, null, Now, Now);

        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);

        var processors = new Mock<IProcessorRepository>();
        processors
            .Setup(repo => repo.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(handoffs ?? []);

        var jobQueue = new Mock<IJobQueueRepository>();
        jobQueue.Setup(repo => repo.ListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(jobs ?? []);

        var catalogue = new Mock<IMovieCatalogRepository>();
        catalogue.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(films ?? []);

        var check = new ProcessorOutputSafetyNetHealthCheck(libraries.Object, processors.Object, jobQueue.Object, catalogue.Object);
        return await check.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Two_stray_files_read_differently()
    {
        WriteReadyFile(Path.Combine(_root, "Sintel.2010.1080p.mkv"));
        WriteReadyFile(Path.Combine(_root, "Big.Buck.Bunny.2008.1080p.mkv"));

        SameProblemReadsDifferentlyPerItemTests.AssertEachReadsDifferently(await RunAsync());
    }
}
