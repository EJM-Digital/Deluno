using System.Runtime.CompilerServices;
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
/// #1529, golden-path run R2A (6 Nov): Weir was asked for a film again after its file left the library. It reported the
/// cleaned output it already held, untouched since the first import, and Deluno answered "already handled" without queuing
/// an import, so the hand-off closed and the film stayed without a file. A film with no file is waiting for that output.
/// </summary>
public sealed class AnUnchangedOutputIsImportedAgainWhenItsFilmLostItsFileTests
{
    private const string Release = "Gpagain.2020.1080p.WEB-DL.x264-GOLDEN";

    [Theory]
    [InlineData(false, "job-2", false)]
    [InlineData(true, null, true)]
    public async Task An_unchanged_output_is_imported_again_only_when_its_film_has_no_file(bool filmHasFile, string? expectedJob, bool expectedAlreadyImported)
    {
        var root = Path.Combine(Path.GetTempPath(), "deluno-redownload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var output = Path.Combine(root, Release + ".mkv");
            await File.WriteAllTextAsync(output, "cleaned");
            File.SetLastWriteTimeUtc(output, DateTime.UtcNow.AddMinutes(-10));
            var library = (LibraryItem)RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem)) with { Id = "lib-1", MediaType = "movies" };
            var key = $"processor-output:{library.Id}:{Path.GetFullPath(output).ToLowerInvariant()}";
            var firstImport = (JobQueueItem)RuntimeHelpers.GetUninitializedObject(typeof(JobQueueItem)) with
            {
                Id = "job-first",
                JobType = "filesystem.import.execute",
                Status = "completed",
                IdempotencyKey = key,
                CompletedUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
            };
            var jobs = new Mock<IJobQueueRepository>();
            jobs.Setup(repository => repository.FindRecentDispatchLinkAsync("client-1", Release, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DispatchCatalogueLink("dispatch-2", "movie", "movie-1", LibraryId: "lib-1"));
            jobs.Setup(repository => repository.ListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<JobQueueItem> { firstImport });
            var film = (MovieListItem)RuntimeHelpers.GetUninitializedObject(typeof(MovieListItem));
            var movies = new Mock<IMovieCatalogRepository>();
            movies.Setup(repository => repository.GetByIdAsync("movie-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(film with { Id = "movie-1", Title = "Gpagain", ReleaseYear = 2020, HasFile = filmHasFile, CreatedUtc = DateTimeOffset.UtcNow.AddHours(-1) });
            var scheduler = new Mock<IJobScheduler>();
            scheduler.Setup(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((EnqueueJobRequest request, CancellationToken _) => (JobQueueItem)RuntimeHelpers.GetUninitializedObject(typeof(JobQueueItem)) with { Id = "job-2" });
            var handoff = new ProcessorHandoffItem(
                "handoff-2", "lib-1", "movies", "client-1", "queue-1", Release,
                Path.Combine(root, "downloads"), "Weir", "waiting", null, null, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

            var result = await new ProcessorOutputImportQueue(jobs.Object, scheduler.Object, movies.Object, Mock.Of<ISeriesCatalogRepository>())
                .QueueAsync(library, handoff, output, "processor", CancellationToken.None);

            Assert.Equal(expectedAlreadyImported, result.AlreadyImported);
            Assert.Equal(expectedJob, result.JobId);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
