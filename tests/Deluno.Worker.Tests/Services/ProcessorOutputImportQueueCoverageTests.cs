using System.Runtime.CompilerServices;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// The paths of a processor's output import that decide between "import it", "already imported" and "its title is gone" (#1155, #1226, #1529):
/// a film or show removed and added again, an episode as well as a film, and a catalogue blob that is not JSON.
/// </summary>
public sealed class ProcessorOutputImportQueueCoverageTests
{
    private const string Release = "Gpagain.2020.1080p.WEB-DL.x264-GOLDEN";

    private static T Blank<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static MovieListItem Film(string id, bool hasFile = false, string? metadata = null)
        => Blank<MovieListItem>() with { Id = id, Title = "Gpagain", ReleaseYear = 2020, HasFile = hasFile, CreatedUtc = DateTimeOffset.UtcNow, MetadataJson = metadata };

    private static SeriesListItem Show(string id, string title = "Gpagain", DateTimeOffset? created = null, string? metadata = null)
        => Blank<SeriesListItem>() with { Id = id, Title = title, StartYear = 2008, CreatedUtc = created ?? DateTimeOffset.UtcNow, MetadataJson = metadata };

    private static ProcessorHandoffItem Handoff(string release = Release, string mediaType = "movies")
        => new("handoff-1", "lib-1", mediaType, "client-1", "queue-1", release, "/downloads/x", "Weir", "waiting", null, null, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static LibraryItem Library(string mediaType = "movies")
        => Blank<LibraryItem>() with { Id = "lib-1", MediaType = mediaType };

    private static Mock<IJobScheduler> Scheduler()
    {
        var scheduler = new Mock<IJobScheduler>();
        scheduler.Setup(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((EnqueueJobRequest _, CancellationToken _) => Blank<JobQueueItem>() with { Id = "job-new" });
        return scheduler;
    }

    private static Mock<IJobQueueRepository> JobsWithLink(DispatchCatalogueLink? link)
    {
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(repository => repository.FindRecentDispatchLinkAsync("client-1", It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(link);
        return jobs;
    }

    // ---- FindReAddedTitleAsync ----

    [Fact]
    public async Task A_grab_with_no_catalogue_link_or_no_readable_title_finds_no_re_added_title()
    {
        var movies = new Mock<IMovieCatalogRepository>();
        var series = new Mock<ISeriesCatalogRepository>();
        var link = new DispatchCatalogueLink("d", "movie", "m-old");

        Assert.Null(await CatalogueImportNaming.FindReAddedTitleAsync(null, Release, movies.Object, series.Object, CancellationToken.None));
        Assert.Null(await CatalogueImportNaming.FindReAddedTitleAsync(link, "", movies.Object, series.Object, CancellationToken.None));
    }

    [Fact]
    public async Task A_film_removed_and_added_again_is_found_by_its_name_and_year_and_not_by_another_year()
    {
        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repository => repository.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<MovieListItem> { Film("m-new") });
        var series = new Mock<ISeriesCatalogRepository>();
        var link = new DispatchCatalogueLink("d", "movie", "m-old");

        var found = await CatalogueImportNaming.FindReAddedTitleAsync(link, Release, movies.Object, series.Object, CancellationToken.None);
        var otherYear = await CatalogueImportNaming.FindReAddedTitleAsync(link, "Gpagain.1999.1080p.WEB-DL-GOLDEN", movies.Object, series.Object, CancellationToken.None);

        Assert.Equal("m-new", found!.EntityId);
        Assert.Null(otherYear);
    }

    [Fact]
    public async Task A_show_removed_and_added_again_is_found_by_its_name_and_an_episode_grab_is_not_looked_for()
    {
        var movies = new Mock<IMovieCatalogRepository>();
        var series = new Mock<ISeriesCatalogRepository>();
        series.Setup(repository => repository.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SeriesListItem> { Show("s-new"), Show("s-other", "Another Show") });

        var found = await CatalogueImportNaming.FindReAddedTitleAsync(new DispatchCatalogueLink("d", "series", "s-old"), Release, movies.Object, series.Object, CancellationToken.None);
        var missing = await CatalogueImportNaming.FindReAddedTitleAsync(new DispatchCatalogueLink("d", "series", "s-old"), "Nothing.Like.It.2001.1080p", movies.Object, series.Object, CancellationToken.None);
        var episode = await CatalogueImportNaming.FindReAddedTitleAsync(new DispatchCatalogueLink("d", "episode", "e-old"), Release, movies.Object, series.Object, CancellationToken.None);

        Assert.Equal("s-new", found!.EntityId);
        Assert.Null(missing);
        Assert.Null(episode);
    }

    // ---- SeriesIdOfAsync / RemovedSinceGrabAsync ----

    [Fact]
    public async Task The_show_of_a_grab_is_read_from_the_show_or_from_the_episode_and_a_film_has_none()
    {
        var series = new Mock<ISeriesCatalogRepository>();
        series.Setup(repository => repository.GetEpisodeSeriesIdAsync("e-1", It.IsAny<CancellationToken>())).ReturnsAsync("s-1");

        Assert.Equal("s-1", await CatalogueImportNaming.SeriesIdOfAsync(new DispatchCatalogueLink("d", "episode", "e-1"), series.Object, CancellationToken.None));
        Assert.Equal("s-9", await CatalogueImportNaming.SeriesIdOfAsync(new DispatchCatalogueLink("d", "series", "s-9"), series.Object, CancellationToken.None));
        Assert.Null(await CatalogueImportNaming.SeriesIdOfAsync(new DispatchCatalogueLink("d", "movie", "m-1"), series.Object, CancellationToken.None));
    }

    [Fact]
    public async Task What_was_removed_since_the_grab_is_named_in_plain_words()
    {
        var movies = new Mock<IMovieCatalogRepository>();
        var series = new Mock<ISeriesCatalogRepository>();
        series.Setup(repository => repository.GetEpisodeSeriesIdAsync("e-has-show", It.IsAny<CancellationToken>())).ReturnsAsync("s-gone");
        Task<string?> Removed(DispatchCatalogueLink? link, IReadOnlyList<LibraryItem>? libraries = null)
            => CatalogueImportNaming.RemovedSinceGrabAsync(link, libraries, movies.Object, series.Object, CancellationToken.None);

        Assert.Null(await Removed(null));
        Assert.Equal("the library that grabbed it was removed from Deluno", await Removed(new DispatchCatalogueLink("d", "movie", "m-1", LibraryId: "lib-gone"), [Library()]));
        Assert.Null(await Removed(new DispatchCatalogueLink("d", "movie", "", LibraryId: "lib-1"), [Library()]));
        Assert.Equal("the film was removed from Deluno", await Removed(new DispatchCatalogueLink("d", "movie", "m-1")));
        Assert.Equal("the episode was removed from Deluno", await Removed(new DispatchCatalogueLink("d", "episode", "e-none")));
        Assert.Equal("the show was removed from Deluno", await Removed(new DispatchCatalogueLink("d", "episode", "e-has-show")));
        Assert.Equal("the show was removed from Deluno", await Removed(new DispatchCatalogueLink("d", "series", "s-gone")));
        Assert.Null(await Removed(new DispatchCatalogueLink("d", "collection", "c-1")));
    }

    // ---- ResolveAsync: a catalogue blob that is not JSON ----

    [Fact]
    public async Task A_catalogue_entry_whose_stored_metadata_is_not_JSON_is_still_named_from_its_other_fields()
    {
        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repository => repository.GetByIdAsync("m-1", It.IsAny<CancellationToken>())).ReturnsAsync(Film("m-1", metadata: "not json {"));
        movies.Setup(repository => repository.GetByIdAsync("m-2", It.IsAny<CancellationToken>())).ReturnsAsync(Film("m-2", metadata: "{\"studio\":\"Acme\"}"));
        var series = new Mock<ISeriesCatalogRepository>();
        series.Setup(repository => repository.GetByIdAsync("s-1", It.IsAny<CancellationToken>())).ReturnsAsync(Show("s-1", metadata: "not json {"));
        series.Setup(repository => repository.GetByIdAsync("s-2", It.IsAny<CancellationToken>())).ReturnsAsync(Show("s-2", metadata: "{\"Network\":7,\"tvdbId\":\"81189\"}"));

        var brokenFilm = await CatalogueImportNaming.ResolveAsync(new DispatchCatalogueLink("d", "movie", "m-1"), movies.Object, series.Object, CancellationToken.None);
        var studioFilm = await CatalogueImportNaming.ResolveAsync(new DispatchCatalogueLink("d", "movie", "m-2"), movies.Object, series.Object, CancellationToken.None);
        var brokenShow = await CatalogueImportNaming.ResolveAsync(new DispatchCatalogueLink("d", "series", "s-1"), movies.Object, series.Object, CancellationToken.None);
        var tvdbShow = await CatalogueImportNaming.ResolveAsync(new DispatchCatalogueLink("d", "series", "s-2"), movies.Object, series.Object, CancellationToken.None);

        Assert.Equal("Gpagain", brokenFilm.Title);
        Assert.Null(brokenFilm.Studio);
        Assert.Equal("Acme", studioFilm.Studio);
        Assert.Equal("Gpagain", brokenShow.Title);
        Assert.Null(brokenShow.TvDbId);
        Assert.Equal("81189", tvdbShow.TvDbId);
        Assert.Null(tvdbShow.Network);
    }

    // ---- QueueAsync ----

    [Fact]
    public async Task A_download_already_imported_is_not_imported_a_second_time()
    {
        var jobs = JobsWithLink(null);
        jobs.Setup(repository => repository.ListDownloadDispatchesAsync(It.IsAny<int>(), null, It.IsAny<CancellationToken>(), true, "client-1"))
            .ReturnsAsync(new List<DownloadDispatchItem>
            {
                Blank<DownloadDispatchItem>() with { DownloadClientId = "client-1", ReleaseName = Release, ImportStatus = "imported", CreatedUtc = DateTimeOffset.UtcNow }
            });
        var scheduler = new Mock<IJobScheduler>(MockBehavior.Strict);

        var result = await new ProcessorOutputImportQueue(jobs.Object, scheduler.Object, Mock.Of<IMovieCatalogRepository>(), Mock.Of<ISeriesCatalogRepository>())
            .QueueAsync(Library(), Handoff(), "/refined/x.mkv", "processor", CancellationToken.None);

        Assert.True(result.AlreadyImported);
        Assert.Null(result.JobId);
    }

    [Fact]
    public async Task A_film_removed_and_added_again_that_already_has_a_file_still_receives_the_output_under_its_new_id()
    {
        var jobs = JobsWithLink(new DispatchCatalogueLink("d-1", "movie", "m-old", LibraryId: "lib-1"));
        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repository => repository.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<MovieListItem> { Film("m-new", hasFile: true) });
        movies.Setup(repository => repository.GetByIdAsync("m-new", It.IsAny<CancellationToken>())).ReturnsAsync(Film("m-new", hasFile: true));

        var result = await new ProcessorOutputImportQueue(jobs.Object, Scheduler().Object, movies.Object, Mock.Of<ISeriesCatalogRepository>())
            .QueueAsync(Library(), Handoff(), "/refined/x.mkv", "processor", CancellationToken.None);

        Assert.Equal("job-new", result.JobId);
        Assert.Equal("m-new", result.EntityId);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task An_unchanged_output_is_imported_again_for_an_episode_only_when_the_episode_has_no_file(bool episodeHasFile, bool expectedImport)
    {
        var root = Path.Combine(Path.GetTempPath(), "deluno-episode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string release = "Gpagain.S01E02.1080p.WEB-DL.x264-GOLDEN";
            var output = Path.Combine(root, release + ".mkv");
            await File.WriteAllTextAsync(output, "cleaned");
            File.SetLastWriteTimeUtc(output, DateTime.UtcNow.AddMinutes(-10));
            var library = Library("tv");
            var key = $"processor-output:{library.Id}:{Path.GetFullPath(output).ToLowerInvariant()}";
            var jobs = JobsWithLink(new DispatchCatalogueLink("d-2", "episode", "e-2", LibraryId: "lib-1"));
            jobs.Setup(repository => repository.ListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<JobQueueItem>
                {
                    Blank<JobQueueItem>() with { Id = "job-first", JobType = "filesystem.import.execute", Status = "completed", IdempotencyKey = key, CompletedUtc = DateTimeOffset.UtcNow.AddMinutes(-5) }
                });
            var series = new Mock<ISeriesCatalogRepository>();
            series.Setup(repository => repository.GetEpisodeSeriesIdAsync("e-2", It.IsAny<CancellationToken>())).ReturnsAsync("s-1");
            series.Setup(repository => repository.GetByIdAsync("s-1", It.IsAny<CancellationToken>())).ReturnsAsync(Show("s-1"));
            series.Setup(repository => repository.GetEpisodeFilePathAsync("e-2", It.IsAny<CancellationToken>()))
                .ReturnsAsync(episodeHasFile ? "/library/Gpagain/S01E02.mkv" : null);

            var result = await new ProcessorOutputImportQueue(jobs.Object, Scheduler().Object, Mock.Of<IMovieCatalogRepository>(), series.Object)
                .QueueAsync(library, Handoff(release, "tv"), output, "processor", CancellationToken.None);

            Assert.Equal(expectedImport, result.JobId is not null);
            Assert.Equal(!expectedImport, result.AlreadyImported);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task An_unchanged_output_with_no_grab_found_is_not_imported_again()
    {
        var root = Path.Combine(Path.GetTempPath(), "deluno-nolink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var output = Path.Combine(root, Release + ".mkv");
            await File.WriteAllTextAsync(output, "cleaned");
            File.SetLastWriteTimeUtc(output, DateTime.UtcNow.AddMinutes(-10));
            var key = $"processor-output:lib-1:{Path.GetFullPath(output).ToLowerInvariant()}";
            var jobs = JobsWithLink(null);
            jobs.Setup(repository => repository.ListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<JobQueueItem>
                {
                    Blank<JobQueueItem>() with { Id = "job-first", JobType = "filesystem.import.execute", Status = "completed", IdempotencyKey = key, CompletedUtc = DateTimeOffset.UtcNow.AddMinutes(-5) }
                });

            var result = await new ProcessorOutputImportQueue(jobs.Object, new Mock<IJobScheduler>(MockBehavior.Strict).Object, Mock.Of<IMovieCatalogRepository>(), Mock.Of<ISeriesCatalogRepository>())
                .QueueAsync(Library(), Handoff(), output, "processor", CancellationToken.None);

            Assert.True(result.AlreadyImported);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // ---- the hand-off questions a watcher asks ----

    [Fact]
    public async Task A_title_that_has_a_file_from_another_download_is_told_apart_for_a_film_an_episode_and_nothing()
    {
        var later = DateTimeOffset.UtcNow;
        Mock<IJobQueueRepository> Jobs(DispatchCatalogueLink? link, string entityId)
        {
            var jobs = JobsWithLink(link);
            jobs.Setup(repository => repository.ListDownloadDispatchesAsync(500, null, It.IsAny<CancellationToken>(), true, null))
                .ReturnsAsync(new List<DownloadDispatchItem>
                {
                    Blank<DownloadDispatchItem>() with { EntityId = entityId, ImportStatus = "imported", CreatedUtc = later, DownloadClientId = "client-2", ReleaseName = "Another.Release" }
                });
            return jobs;
        }

        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repository => repository.GetByIdAsync("m-1", It.IsAny<CancellationToken>())).ReturnsAsync(Film("m-1", hasFile: true));
        var series = new Mock<ISeriesCatalogRepository>();
        series.Setup(repository => repository.GetEpisodeFilePathAsync("e-1", It.IsAny<CancellationToken>())).ReturnsAsync("/library/e1.mkv");
        ProcessorOutputImportQueue Queue(Mock<IJobQueueRepository> jobs) => new(jobs.Object, Scheduler().Object, movies.Object, series.Object);

        Assert.False(await Queue(Jobs(null, "m-1")).TitleHasFileFromAnotherDownloadAsync(Handoff(), CancellationToken.None));
        Assert.True(await Queue(Jobs(new DispatchCatalogueLink("d", "movie", "m-1"), "m-1")).TitleHasFileFromAnotherDownloadAsync(Handoff(), CancellationToken.None));
        Assert.True(await Queue(Jobs(new DispatchCatalogueLink("d", "episode", "e-1"), "e-1")).TitleHasFileFromAnotherDownloadAsync(Handoff(), CancellationToken.None));
        Assert.False(await Queue(Jobs(new DispatchCatalogueLink("d", "series", "s-1"), "s-1")).TitleHasFileFromAnotherDownloadAsync(Handoff(), CancellationToken.None));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task A_show_added_again_without_the_episode_file_is_the_title_a_hand_off_came_back_to_only_when_added_after_it(bool addedAfterHandoff, bool expectedFound)
    {
        var jobs = JobsWithLink(new DispatchCatalogueLink("d", "episode", "e-1"));
        var added = addedAfterHandoff ? DateTimeOffset.UtcNow : DateTimeOffset.UnixEpoch.AddDays(-1);
        var series = new Mock<ISeriesCatalogRepository>();
        series.Setup(repository => repository.GetEpisodeSeriesIdAsync("e-1", It.IsAny<CancellationToken>())).ReturnsAsync("s-1");
        series.Setup(repository => repository.GetByIdAsync("s-1", It.IsAny<CancellationToken>())).ReturnsAsync(Show("s-1", created: added));

        var cameBack = await new ProcessorOutputImportQueue(jobs.Object, Scheduler().Object, Mock.Of<IMovieCatalogRepository>(), series.Object)
            .TitleCameBackWithoutItsFileAsync(Handoff("Gpagain.S01E01.1080p", "tv"), CancellationToken.None);

        Assert.Equal(expectedFound, cameBack is not null);
    }
}
