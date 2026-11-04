using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Worker.HealthChecks.Checks;
using Deluno.Worker.Services;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>#921, #1138, #1155: every branch of the processor output safety net, on a real temporary folder.</summary>
public sealed class ProcessorOutputSafetyNetBranchesTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");
    private readonly string _root = Directory.CreateTempSubdirectory("deluno-safetynet-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Ready(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "content");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        return path;
    }

    private static MovieListItem Film(string title, int? year, bool hasFile = true)
        => new("movie-" + title, title, year, null, true, hasFile, null, null, null, null, null, null, null, [], null, null, null, null, Now, Now);

    private static JobQueueItem ImportJob(string source, string status)
        => TestJobs.Create("filesystem.import.execute", payloadJson: "{\"preview\":{\"sourcePath\":\"" + source.Replace("\\", "\\\\") + "\"}}", status: status);

    private static ProcessorHandoffItem Handoff(string status, string? outputPath = null)
        => new("handoff-1", "library-1", "movie", "client-1", "queue-1", "Sintel.2010.1080p", "/downloads/Sintel.2010.1080p", "Weir", status, outputPath, null, null, Now.AddHours(-1), Now);

    private async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(
        string mediaType = "movies",
        string workflow = "refine-before-import",
        string? output = "<root>",
        JobQueueItem[]? jobs = null,
        MovieListItem[]? films = null,
        ProcessorHandoffItem[]? finished = null,
        Func<string, IEnumerable<string>>? listEntries = null,
        Func<string, IEnumerable<string>>? listFiles = null,
        ProcessorOutputSweep? sweep = null)
    {
        var library = new LibraryItem(
            "library-1", "Films", mediaType, "main", "/library", null, null, null, null,
            true, true, workflow, "Weir", output == "<root>" ? _root : output, 360, "block",
            true, true, true, 6, 6, 10, null, null, "active", false, null, null, Now, Now);
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);
        var processors = new Mock<IProcessorRepository>();
        processors.Setup(repo => repo.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> statuses, CancellationToken _) => statuses.Contains(ProcessorHandoffStatuses.Completed) ? finished ?? [] : []);
        var queue = new Mock<IJobQueueRepository>();
        queue.Setup(repo => repo.ListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(jobs ?? []);
        var catalogue = new Mock<IMovieCatalogRepository>();
        catalogue.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(films ?? []);
        var check = new ProcessorOutputSafetyNetHealthCheck(libraries.Object, processors.Object, queue.Object, catalogue.Object, sweep, listEntries, listFiles);
        Assert.Equal(HealthCheckCategories.Processor, check.Category);
        return await check.RunAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("import-directly", "<root>")]
    [InlineData("refine-before-import", null)]
    [InlineData("refine-before-import", "  ")]
    public async Task A_library_that_hands_nothing_to_a_processor_or_has_no_output_folder_is_not_looked_at(string workflow, string? output)
    {
        Ready("Sintel.2010.1080p.mkv");

        Assert.Empty(await RunAsync(workflow: workflow, output: output));
    }

    [Fact]
    public async Task An_output_folder_that_is_not_there_is_skipped()
    {
        Assert.Empty(await RunAsync(output: Path.Combine(_root, "missing")));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Sintel/Sintel.mkv", "Sintel")]
    [InlineData("Sintel.mkv", "Sintel.mkv")]
    [InlineData("../elsewhere/x.mkv", null)]
    [InlineData(".", null)]
    public void A_finished_output_belongs_to_the_first_folder_or_file_under_the_root(string? relative, string? owner)
    {
        var path = relative is null ? null : relative.Length == 0 ? "" : Path.GetFullPath(Path.Combine(_root, relative));

        Assert.Equal(owner, ProcessorOutputSafetyNetHealthCheck.OwnedTopLevelName(_root, path));
    }

    [Fact]
    public async Task A_folder_a_finished_hand_off_owns_is_not_listed_and_one_it_does_not_own_is_listed()
    {
        Ready("Owned/Sintel.2010.1080p.mkv");
        Ready("Other/Alien.1979.1080p.mkv");

        var outcomes = await RunAsync(finished: [Handoff(ProcessorHandoffStatuses.Completed, Path.Combine(_root, "Owned", "Sintel.2010.1080p.mkv"))]);

        Assert.Equal(["processor-output:library-1:other"], outcomes.Select(o => o.Id));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("denied")]
    public async Task An_output_folder_that_cannot_be_listed_is_skipped(string kind)
    {
        Ready("Sintel.2010.1080p.mkv");

        var outcomes = await RunAsync(listEntries: _ => throw (kind == "io" ? new IOException("gone") : new UnauthorizedAccessException("no")));

        Assert.Empty(outcomes);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("denied")]
    public async Task A_folder_whose_files_cannot_be_listed_is_left_alone(string kind)
    {
        Ready("Lioness.S02/Lioness.S02E01.mkv");

        var outcomes = await RunAsync(mediaType: "tv", listFiles: _ => throw (kind == "io" ? new IOException("gone") : new UnauthorizedAccessException("no")));

        Assert.Empty(outcomes);
    }

    [Fact]
    public async Task A_folder_with_a_file_still_being_written_or_all_files_already_queued_is_left_alone()
    {
        var busy = Ready("Busy.S01/Busy.S01E01.mkv");
        File.SetLastWriteTimeUtc(busy, DateTime.UtcNow);
        var queued = Ready("Queued.S01/Queued.S01E01.mkv");

        Assert.Empty(await RunAsync(mediaType: "tv", jobs: [ImportJob(queued, "queued")]));
    }

    [Fact]
    public async Task A_folder_with_one_file_queued_and_one_not_is_listed()
    {
        var queued = Ready("Mixed.S01/Mixed.S01E01.mkv");
        Ready("Mixed.S01/Mixed.S01E02.mkv");

        var outcome = Assert.Single(await RunAsync(mediaType: "tv", jobs: [ImportJob(queued, "queued")]));

        Assert.Equal("The Films library's processor output folder has a season folder Deluno cannot match to a hand-off: Mixed.S01.", outcome.Message);
        Assert.Equal("Import Mixed.S01, or clear it.", outcome.Fix);
        Assert.Equal("Unexpected files in the Films library's processor output folder", outcome.Name);
        Assert.Equal("Mixed.S01 should not be in the Films library's processor output folder: it only occurs after something else already went wrong, or another app shares this folder.", outcome.Cause);
    }

    [Fact]
    public async Task A_dead_lettered_import_no_longer_keeps_its_file_out_of_the_list()
    {
        var file = Ready("Sintel.2010.1080p.mkv");

        var outcome = Assert.Single(await RunAsync(jobs: [ImportJob(file, "dead-letter")]));

        Assert.Equal("file", System.Text.Json.JsonDocument.Parse(outcome.DetailsJson!).RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_film_folder_with_one_video_is_a_film_folder_and_with_two_it_is_a_season_folder()
    {
        Ready("One/One.2010.mkv");
        Ready("Two/Two.2011.a.mkv");
        Ready("Two/Two.2011.b.mkv");

        var outcomes = await RunAsync();

        Assert.Contains("a film folder Deluno cannot match to a hand-off: One.", outcomes.Single(o => o.Id.EndsWith(":one", StringComparison.Ordinal)).Message);
        Assert.Contains("a season folder Deluno cannot match to a hand-off: Two.", outcomes.Single(o => o.Id.EndsWith(":two", StringComparison.Ordinal)).Message);
    }

    [Fact]
    public async Task A_folder_of_scraps_is_not_media_and_its_fix_is_to_clear_it()
    {
        Ready("Scraps/readme.nfo");

        var outcome = Assert.Single(await RunAsync());

        Assert.Contains("\"isMedia\":false", outcome.DetailsJson);
        Assert.Equal("Clear Scraps, or leave it for now.", outcome.Fix);
    }

    [Fact]
    public async Task A_loose_file_that_is_media_is_offered_import_and_one_that_is_not_is_offered_clearing()
    {
        Ready("Alien.1979.mkv");
        Ready("notes.txt");

        var outcomes = await RunAsync();

        var media = outcomes.Single(o => o.Id.EndsWith(":alien.1979.mkv", StringComparison.Ordinal));
        var scraps = outcomes.Single(o => o.Id.EndsWith(":notes.txt", StringComparison.Ordinal));
        Assert.Equal("The Films library's processor output folder has a file Deluno cannot match to a hand-off: Alien.1979.mkv.", media.Message);
        Assert.Equal("Import Alien.1979.mkv, or clear it.", media.Fix);
        Assert.Contains("\"kind\":\"file\"", media.DetailsJson);
        Assert.Equal("The Films library's processor output folder has files that are not media: notes.txt.", scraps.Message);
        Assert.Equal("Clear notes.txt, or leave it for now.", scraps.Fix);
        Assert.Contains("\"kind\":\"scraps\"", scraps.DetailsJson);
    }

    [Fact]
    public async Task A_second_copy_in_a_folder_is_said_so_with_keep_and_remove_wording()
    {
        Ready("Pulp.Fiction.1994.1080p/Pulp.Fiction.1994.1080p.mkv");

        var outcome = Assert.Single(await RunAsync(films: [Film("Pulp Fiction", 1994)]));

        Assert.Equal("A second copy of Pulp Fiction (1994) is waiting in the Films library's processor output folder", outcome.Name);
        Assert.Equal("The Films library already has Pulp Fiction (1994), and a second copy is sitting in its processor output folder: Pulp.Fiction.1994.1080p.", outcome.Message);
        Assert.Equal("A processor made more than one file for Pulp Fiction (1994), or it was fetched twice.", outcome.Cause);
        Assert.Equal("Remove the spare copy of Pulp Fiction (1994) (Pulp.Fiction.1994.1080p), or keep it for now.", outcome.Fix);
        Assert.Contains("\"kind\":\"folder\"", outcome.DetailsJson);
    }

    [Fact]
    public async Task A_second_copy_that_is_a_loose_file_is_said_so_and_a_film_with_no_year_is_never_held()
    {
        Ready("Alien.1979.1080p.mkv");

        var held = Assert.Single(await RunAsync(films: [Film("Alien", 1979)]));
        var noYear = Assert.Single(await RunAsync(films: [Film("Alien", null)]));

        Assert.Contains("\"kind\":\"file\"", held.DetailsJson);
        Assert.Contains("second copy", held.Message);
        Assert.DoesNotContain("second copy", noYear.Message);
    }

    [Fact]
    public async Task A_second_copy_still_being_written_or_with_an_import_still_running_is_not_listed_but_one_with_a_finished_import_is()
    {
        var copying = Ready("Alien.1979.1080p.mkv");
        File.SetLastWriteTimeUtc(copying, DateTime.UtcNow);
        var films = new[] { Film("Alien", 1979) };

        Assert.Empty(await RunAsync(films: films));
        File.SetLastWriteTimeUtc(copying, DateTime.UtcNow.AddMinutes(-5));
        Assert.Empty(await RunAsync(films: films, jobs: [ImportJob(copying, "queued")]));
        Assert.Single(await RunAsync(films: films, jobs: [ImportJob(copying, "completed")]));
        Assert.Single(await RunAsync(films: films, jobs: [ImportJob(copying, "failed")]));
    }

    [Fact]
    public async Task A_second_copy_whose_files_cannot_be_listed_is_left_alone()
    {
        Ready("Alien.1979.1080p/Alien.1979.1080p.mkv");

        Assert.Empty(await RunAsync(films: [Film("Alien", 1979)], listFiles: _ => throw new IOException("gone")));
        Assert.Empty(await RunAsync(films: [Film("Alien", 1979)], listFiles: _ => throw new UnauthorizedAccessException("no")));
    }

    [Fact]
    public async Task An_empty_folder_that_looks_like_a_film_we_hold_is_left_alone()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Alien.1979.1080p"));

        Assert.Empty(await RunAsync(films: [Film("Alien", 1979)]));
    }

    [Fact]
    public async Task A_tv_library_never_calls_a_folder_a_second_copy_of_a_film()
    {
        Ready("Alien.1979.1080p/Alien.1979.1080p.mkv");

        var outcome = Assert.Single(await RunAsync(mediaType: "tv", films: [Film("Alien", 1979)]));

        Assert.DoesNotContain("second copy", outcome.Message);
    }

    [Fact]
    public async Task A_stray_non_video_file_is_never_taken_for_a_film()
    {
        Ready("Alien.1979.nfo");

        var outcome = Assert.Single(await RunAsync(films: [Film("Alien", 1979)]));

        Assert.DoesNotContain("second copy", outcome.Message);
    }

    private ProcessorOutputSweep Sweep(Exception failure)
    {
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        return new ProcessorOutputSweep(
            libraries.Object, new Mock<IProcessorRepository>().Object, new Mock<IJobQueueRepository>().Object,
            new Mock<IMovieCatalogRepository>().Object, new Mock<ISeriesCatalogRepository>().Object,
            new Mock<IActivityFeedRepository>().Object, new Mock<IHealthCheckRepository>().Object);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("denied")]
    [InlineData("invalid")]
    public async Task A_sweep_that_fails_for_a_file_reason_does_not_stop_the_list(string kind)
    {
        Ready("Alien.1979.mkv");
        Exception failure = kind switch { "io" => new IOException("x"), "denied" => new UnauthorizedAccessException("x"), _ => new InvalidOperationException("x") };

        Assert.Single(await RunAsync(sweep: Sweep(failure)));
    }

    [Fact]
    public async Task A_sweep_that_finishes_leaves_the_list_as_it_is_and_an_import_job_naming_no_source_keeps_nothing_out()
    {
        Ready("Alien.1979.mkv");
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repo => repo.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var sweep = new ProcessorOutputSweep(
            libraries.Object, new Mock<IProcessorRepository>().Object, new Mock<IJobQueueRepository>().Object,
            new Mock<IMovieCatalogRepository>().Object, new Mock<ISeriesCatalogRepository>().Object,
            new Mock<IActivityFeedRepository>().Object, new Mock<IHealthCheckRepository>().Object);
        var noSource = TestJobs.Create("filesystem.import.execute", payloadJson: "{}", status: "queued");

        Assert.Single(await RunAsync(sweep: sweep, jobs: [noSource, TestJobs.Create("library.search.movies", payloadJson: "{}")]));
    }

    [Fact]
    public async Task A_sweep_that_fails_for_any_other_reason_is_not_hidden()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(sweep: Sweep(new ArgumentException("a bug"))));
    }

    [Theory]
    [InlineData("Pulp.Fiction.1994.1080p.mkv", "pulpfiction|1994")]
    [InlineData("Pulp Fiction (1994) [1080p]", "pulpfiction|1994")]
    [InlineData("Blade.Runner.2049.2017.mkv", "bladerunner2049|2017")]
    [InlineData("Movie", null)]
    [InlineData("1999.mkv", null)]
    [InlineData("Alien.1899.mkv", null)]
    [InlineData("Alien.2101.mkv", null)]
    [InlineData("Alien.1900.mkv", "alien|1900")]
    [InlineData("Alien.2100.mkv", "alien|2100")]
    public void A_name_is_matched_to_a_film_by_the_words_before_the_year(string name, string? key)
    {
        Assert.Equal(key, SpareCopy.TitleKey(name));
    }

    [Theory]
    [InlineData("Pulp Fiction!", 1994, "pulpfiction|1994")]
    [InlineData("Pulp Fiction", null, null)]
    [InlineData("!!!", 1994, null)]
    public void A_held_film_is_keyed_by_its_letters_and_year(string title, int? year, string? key)
    {
        Assert.Equal(key, SpareCopy.KeyOf(title, year));
    }
}
