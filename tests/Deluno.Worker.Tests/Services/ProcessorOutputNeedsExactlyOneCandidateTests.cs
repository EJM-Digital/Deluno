using Deluno.Filesystem;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// Matching a cleaned output to its hand-off by folder is only safe with ONE candidate under the folder named for the
/// download: with zero or two, the planner imports nothing, rather than guess which file is the film. Real files in a
/// temp folder, the real planner. Refs #1296.
/// </summary>
public sealed class ProcessorOutputNeedsExactlyOneCandidateTests : IDisposable
{
    private const string ReleaseName = "Big.Buck.Bunny.2008.1080p.WEB-DL.x264-DELUNO";

    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"deluno-one-candidate-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Two_cleaned_videos_in_one_hand_off_folder_queue_no_import()
    {
        var scheduler = Scheduler();

        await RunAsync(scheduler, ReleaseName + ".mkv", "Big.Buck.Bunny.2008.Sample.mkv");

        scheduler.Verify(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task No_cleaned_video_in_the_hand_off_folder_queues_no_import()
    {
        var scheduler = Scheduler();

        await RunAsync(scheduler);

        scheduler.Verify(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task One_cleaned_video_is_imported_and_it_is_that_file()
    {
        var scheduler = Scheduler();
        EnqueueJobRequest? queued = null;
        scheduler.Setup(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .Callback<EnqueueJobRequest, CancellationToken>((request, _) => queued = request)
            .ReturnsAsync(Job("import-job"));

        await RunAsync(scheduler, ReleaseName + ".mkv");

        Assert.NotNull(queued);
        Assert.Contains(ReleaseName + ".mkv", queued!.PayloadJson!, StringComparison.Ordinal);
    }

    private async Task RunAsync(Mock<IJobScheduler> scheduler, params string[] videos)
    {
        var outputRoot = Path.Combine(_root, "Refined", "Movies");
        var folder = Path.Combine(outputRoot, ReleaseName);
        Directory.CreateDirectory(folder);
        foreach (var video in videos)
        {
            var path = Path.Combine(folder, video);
            await File.WriteAllTextAsync(path, "refined bytes " + video);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        }

        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(repository => repository.TryClaimScheduledPassAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        jobs.Setup(repository => repository.ListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        jobs.Setup(repository => repository.FindRecentDispatchLinkAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchCatalogueLink("dispatch-now", "movie", "movie-1"));

        var handoff = new ProcessorHandoffItem(
            "handoff-1", "library-1", "movie", "client-1", "queue-1", ReleaseName,
            Path.Combine(_root, "Downloads-Complete", "Movies", ReleaseName), "Weir", "waiting", null, null, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var processors = new Mock<IProcessorRepository>();
        processors.Setup(repository => repository.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([handoff]);

        var now = DateTimeOffset.UnixEpoch;
        var library = new LibraryItem(
            "library-1", "Movies", "movie", "main", _root, null, null, null, null,
            true, true, "refine-before-import", "Weir", outputRoot, 120, "block",
            true, true, true, 6, 6, 10, null, null, "active", false, null, null, now, now);
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([library]);

        await new WorkPlanner(NullLogger<WorkPlanner>.Instance, jobs.Object, new ConfigurationBuilder().Build(), TimeProvider.System)
            .PlanImportAutomationAsync(
                scheduler.Object,
                processors.Object,
                libraries.Object,
                new LibraryAvailabilityService(Mock.Of<IActivityFeedRepository>(), TimeProvider.System, NullLogger<LibraryAvailabilityService>.Instance),
                Mock.Of<IDownloadClientTelemetryService>(),
                Mock.Of<IProcessorConnectionService>(),
                Mock.Of<IActivityFeedRepository>(),
                Mock.Of<IMovieCatalogRepository>(repository => repository.GetByIdAsync("movie-1", It.IsAny<CancellationToken>())
                    == Task.FromResult<Deluno.Movies.Contracts.MovieListItem?>((Deluno.Movies.Contracts.MovieListItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Deluno.Movies.Contracts.MovieListItem)))),
                Mock.Of<ISeriesCatalogRepository>(),
                TimeProvider.System,
                CancellationToken.None);
    }

    private static Mock<IJobScheduler> Scheduler()
    {
        var scheduler = new Mock<IJobScheduler>();
        scheduler.Setup(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Job("import-job"));
        return scheduler;
    }

    private static JobQueueItem Job(string id)
        => new(
            Id: id, JobType: "filesystem.import.execute", Source: "processor-output-watch", Status: "queued", PayloadJson: "{}",
            Attempts: 0, CreatedUtc: DateTimeOffset.UnixEpoch, ScheduledUtc: DateTimeOffset.UnixEpoch, StartedUtc: null,
            CompletedUtc: null, LeasedUntilUtc: null, WorkerId: null, LastError: null, RelatedEntityType: "movie",
            RelatedEntityId: "movie-1", IdempotencyKey: null, DedupeKey: null, MaxAttempts: 3, LastAttemptUtc: null, NextAttemptUtc: null);
}
