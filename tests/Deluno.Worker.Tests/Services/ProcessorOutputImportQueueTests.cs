using System.Runtime.CompilerServices;
using System.Text.Json;
using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
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
            .ReturnsAsync(movie with { Id = "movie-13", Title = "Dementia 13", ReleaseYear = 1963, ImdbId = "tt0056983" });
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
    }
}
