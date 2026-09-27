using System.Runtime.CompilerServices;
using System.Text.Json;
using Deluno.Filesystem;
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
/// #526: a processor's completed output is imported as the title that was
/// grabbed, into the library that grabbed it - not under the file name into the
/// first movie library. Seen on the rig: Weir refined Dementia 13 and it
/// arrived as "Dementia.13.1963.1080p.WEB-DL… (Unknown Year)" in another library.
/// </summary>
public sealed class ProcessorOutputImportQueueTests
{
    [Fact]
    public async Task A_processed_file_is_imported_as_the_grabbed_title_into_the_grabbing_library()
    {
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(repository => repository.FindRecentDispatchLinkAsync("client-1", "Dementia.13.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchCatalogueLink("dispatch-9", "movie", "movie-13", LibraryId: "proof-library"));
        var movie = (MovieListItem)RuntimeHelpers.GetUninitializedObject(typeof(MovieListItem));
        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repository => repository.GetByIdAsync("movie-13", It.IsAny<CancellationToken>()))
            .ReturnsAsync(movie with { Id = "movie-13", Title = "Dementia 13", ReleaseYear = 1963, ImdbId = "tt0056983", OriginalTitle = "Dementia 13" });
        EnqueueJobRequest? queued = null;
        var scheduler = new Mock<IJobScheduler>();
        scheduler.Setup(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EnqueueJobRequest, CancellationToken>((request, _) => queued = request)
            .ReturnsAsync((EnqueueJobRequest request, CancellationToken _) => (JobQueueItem)RuntimeHelpers.GetUninitializedObject(typeof(JobQueueItem)) with { Id = "job-1" });

        var library = (LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem)) with { Id = "proof-library", MediaType = "movies" };
        var handoff = new ProcessorHandoffItem(
            "handoff-1", "proof-library", "movies", "client-1", "queue-1", "Dementia.13.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
            @"C:\Proof\Downloads\Dementia.13.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO", "Weir proof", "submitted", null, null, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var output = @"C:\Proof\Refined\Dementia.13.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO\Dementia.13.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO.mkv";

        var result = await new ProcessorOutputImportQueue(jobs.Object, scheduler.Object, movies.Object, Mock.Of<ISeriesCatalogRepository>())
            .QueueAsync(library, handoff, output, "processor", CancellationToken.None);

        Assert.Equal(new ProcessorOutputImport("job-1", "dispatch-9", "movie-13"), result);
        Assert.NotNull(queued);
        Assert.Equal("movie-13", queued!.RelatedEntityId);
        var request = JsonSerializer.Deserialize<ImportExecuteRequest>(queued.PayloadJson!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("Dementia 13", request.Preview.Title);
        Assert.Equal(1963, request.Preview.Year);
        Assert.Equal("tt0056983", request.Preview.ImdbId);
        Assert.Equal("proof-library", request.Preview.LibraryId);
        Assert.Equal("dispatch-9", request.DispatchId);
        Assert.Equal(output, request.Preview.SourcePath);
        // #760: the {Original Title} naming token is sourced from the
        // catalogue behind the grab, not left null for a processed import.
        Assert.Equal("Dementia 13", request.Preview.OriginalTitle);
    }

    /// <summary>
    /// #617: found by its torrent's hash, the grab names the import even when
    /// the client's name for the release differs from the indexer's.
    /// </summary>
    [Fact]
    public async Task A_processed_file_whose_name_differs_is_found_by_its_torrent_hash()
    {
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(repository => repository.FindDispatchLinkByClientItemAsync("client-1", "queue-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchCatalogueLink("dispatch-kid", "movie", "movie-kid", LibraryId: "proof-library"));
        var movie = (MovieListItem)RuntimeHelpers.GetUninitializedObject(typeof(MovieListItem));
        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repository => repository.GetByIdAsync("movie-kid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(movie with { Id = "movie-kid", Title = "The Kid", ReleaseYear = 1921 });
        var (scheduler, queued) = CapturingScheduler();
        var library = (LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem)) with { Id = "proof-library", MediaType = "movies" };
        var handoff = new ProcessorHandoffItem(
            "handoff-1", "proof-library", "movies", "client-1", "queue-1", "The Kid 1921 CC 1080p BluRay x264-nikt0",
            @"C:\Downloads\Movies\The Kid 1921 CC 1080p BluRay x264-nikt0", "Weir", "submitted", null, null, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        await new ProcessorOutputImportQueue(jobs.Object, scheduler.Object, movies.Object, Mock.Of<ISeriesCatalogRepository>())
            .QueueAsync(library, handoff, @"C:\Refined\Movies\The Kid 1921 CC 1080p BluRay x264-nikt0\The Kid 1921 CC 1080p BluRay x264-nikt0.mkv", "processor", CancellationToken.None);

        var request = JsonSerializer.Deserialize<ImportExecuteRequest>(queued()!.PayloadJson!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("The Kid", request.Preview.Title);
        Assert.Equal("dispatch-kid", request.DispatchId);
    }

    /// <summary>
    /// #617: with no grab found at all, the output file's name is not made
    /// into a title. No title sends the file to import recovery (#417).
    /// </summary>
    [Fact]
    public async Task A_processed_file_with_no_grab_found_is_given_no_title_of_its_own()
    {
        var (scheduler, queued) = CapturingScheduler();
        var library = (LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem)) with { Id = "proof-library", MediaType = "movies" };
        var handoff = new ProcessorHandoffItem(
            "handoff-1", "proof-library", "movies", "client-1", "queue-1", "The Kid 1921 CC 1080p BluRay x264-nikt0",
            @"C:\Downloads\Movies\The Kid 1921 CC 1080p BluRay x264-nikt0", "Weir", "submitted", null, null, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        await new ProcessorOutputImportQueue(Mock.Of<IJobQueueRepository>(), scheduler.Object, Mock.Of<IMovieCatalogRepository>(), Mock.Of<ISeriesCatalogRepository>())
            .QueueAsync(library, handoff, @"C:\Refined\Movies\The Kid 1921 CC 1080p BluRay x264-nikt0\The Kid 1921 CC 1080p BluRay x264-nikt0.mkv", "processor", CancellationToken.None);

        var request = JsonSerializer.Deserialize<ImportExecuteRequest>(queued()!.PayloadJson!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Null(request.Preview.Title);
    }

    /// <summary>
    /// #619: a processor hands a folder back. A film is its one real video; an
    /// episode folder with one video is that video; several episodes are the
    /// season pack, imported whole.
    /// </summary>
    [Fact]
    public void A_handed_back_folder_is_imported_as_what_it_holds()
    {
        var root = Directory.CreateTempSubdirectory("deluno-handback-");
        try
        {
            var film = Directory.CreateDirectory(Path.Combine(root.FullName, "The Kid 1921"));
            File.WriteAllBytes(Path.Combine(film.FullName, "The Kid 1921.mkv"), new byte[4096]);
            File.WriteAllBytes(Path.Combine(film.FullName, "The Kid 1921-sample.mkv"), new byte[8192]);
            File.WriteAllBytes(Path.Combine(film.FullName, "The Kid 1921.nfo"), new byte[99999]);
            Assert.Equal(Path.Combine(film.FullName, "The Kid 1921.mkv"), ProcessorOutputImportQueue.ImportPathFor(film.FullName, "movies"));

            var episode = Directory.CreateDirectory(Path.Combine(root.FullName, "Pioneer One S01E01"));
            File.WriteAllBytes(Path.Combine(episode.FullName, "Pioneer One - S01E01 - Earthfall.mkv"), new byte[64]);
            Assert.Equal(Path.Combine(episode.FullName, "Pioneer One - S01E01 - Earthfall.mkv"), ProcessorOutputImportQueue.ImportPathFor(episode.FullName, "tv"));

            var pack = Directory.CreateDirectory(Path.Combine(root.FullName, "Pioneer One Season 1"));
            File.WriteAllBytes(Path.Combine(pack.FullName, "Pioneer One - S01E01 - Earthfall.mkv"), new byte[64]);
            File.WriteAllBytes(Path.Combine(pack.FullName, "Pioneer One - S01E02 - The Man From Mars.mkv"), new byte[64]);
            Assert.Equal(pack.FullName, ProcessorOutputImportQueue.ImportPathFor(pack.FullName, "tv"));

            var file = Path.Combine(film.FullName, "The Kid 1921.mkv");
            Assert.Equal(file, ProcessorOutputImportQueue.ImportPathFor(file, "movies"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static (Mock<IJobScheduler> Scheduler, Func<EnqueueJobRequest?> Queued) CapturingScheduler()
    {
        EnqueueJobRequest? queued = null;
        var scheduler = new Mock<IJobScheduler>();
        scheduler.Setup(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EnqueueJobRequest, CancellationToken>((request, _) => queued = request)
            .ReturnsAsync((EnqueueJobRequest request, CancellationToken _) => (JobQueueItem)RuntimeHelpers.GetUninitializedObject(typeof(JobQueueItem)) with { Id = "job-1" });
        return (scheduler, () => queued);
    }

    /// <summary>
    /// A film removed from Deluno while its release was being processed is not
    /// brought back under the output file's name when the processed file arrives.
    /// </summary>
    [Fact]
    public async Task A_processed_file_for_a_removed_film_is_not_imported()
    {
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(repository => repository.FindRecentDispatchLinkAsync("client-1", "Dementia.13.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchCatalogueLink("dispatch-9", "movie", "movie-13", LibraryId: "proof-library"));
        var scheduler = new Mock<IJobScheduler>(MockBehavior.Strict);
        var library = (LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem)) with { Id = "proof-library", MediaType = "movies" };
        var handoff = new ProcessorHandoffItem(
            "handoff-1", "proof-library", "movies", "client-1", "queue-1", "Dementia.13.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO",
            @"C:\Proof\Downloads\Dementia.13.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO", "Weir proof", "submitted", null, null, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

        var result = await new ProcessorOutputImportQueue(jobs.Object, scheduler.Object, Mock.Of<IMovieCatalogRepository>(), Mock.Of<ISeriesCatalogRepository>())
            .QueueAsync(library, handoff, @"C:\Proof\Refined\Dementia.13.mkv", "processor", CancellationToken.None);

        Assert.Null(result.JobId);
        Assert.Equal("the film was removed from Deluno", result.NotImportedBecause);
    }

    /// <summary>
    /// #595: an episode search's grab names the episode, and it is imported
    /// under the episode's show. It used to become a new show named after the
    /// release - six Breaking Bad episodes on the soak rig.
    /// </summary>
    [Fact]
    public async Task A_processed_episode_is_imported_under_its_show()
    {
        const string release = "Breaking.Bad.S01E04.1080p.WEB-DL.DDP5.1.H.264-DELUNO";
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(repository => repository.FindRecentDispatchLinkAsync("client-1", release, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchCatalogueLink("dispatch-4", "episode", "episode-104", LibraryId: "tv-library"));
        var series = new Mock<ISeriesCatalogRepository>();
        series.Setup(repository => repository.GetEpisodeSeriesIdAsync("episode-104", It.IsAny<CancellationToken>())).ReturnsAsync("series-bb");
        series.Setup(repository => repository.GetByIdAsync("series-bb", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeriesListItem)RuntimeHelpers.GetUninitializedObject(typeof(SeriesListItem)) with { Id = "series-bb", Title = "Breaking Bad", StartYear = 2008 });
        EnqueueJobRequest? queued = null;
        var scheduler = new Mock<IJobScheduler>();
        scheduler.Setup(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EnqueueJobRequest, CancellationToken>((request, _) => queued = request)
            .ReturnsAsync((EnqueueJobRequest request, CancellationToken _) => (JobQueueItem)RuntimeHelpers.GetUninitializedObject(typeof(JobQueueItem)) with { Id = "job-4" });

        await new ProcessorOutputImportQueue(jobs.Object, scheduler.Object, Mock.Of<IMovieCatalogRepository>(), series.Object)
            .QueueAsync(TvLibrary(), Handoff(release), $@"C:\Refined\TV\{release}\{release}.mkv", "processor", CancellationToken.None);

        var request = JsonSerializer.Deserialize<ImportExecuteRequest>(queued!.PayloadJson!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("Breaking Bad", request.Preview.Title);
        Assert.Equal("series-bb", request.Preview.SeriesId);
        Assert.Equal(2008, request.Preview.Year);
        Assert.Equal("series-bb", queued.RelatedEntityId);
    }

    [Fact]
    public async Task A_processed_episode_removed_since_the_grab_is_not_imported()
    {
        const string release = "Breaking.Bad.S01E04.1080p.WEB-DL.DDP5.1.H.264-DELUNO";
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(repository => repository.FindRecentDispatchLinkAsync("client-1", release, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchCatalogueLink("dispatch-4", "episode", "episode-104", LibraryId: "tv-library"));

        var result = await new ProcessorOutputImportQueue(jobs.Object, new Mock<IJobScheduler>(MockBehavior.Strict).Object, Mock.Of<IMovieCatalogRepository>(), Mock.Of<ISeriesCatalogRepository>())
            .QueueAsync(TvLibrary(), Handoff(release), $@"C:\Refined\TV\{release}.mkv", "processor", CancellationToken.None);

        Assert.Null(result.JobId);
        Assert.Equal("the episode was removed from Deluno", result.NotImportedBecause);
    }

    private static LibraryItem TvLibrary()
        => (LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem)) with { Id = "tv-library", MediaType = "tv" };

    private static ProcessorHandoffItem Handoff(string release)
        => new(
            "handoff-4", "tv-library", "tv", "client-1", "queue-4", release,
            $@"C:\Downloads\TV\{release}", "Weir", "submitted", null, null, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
}
