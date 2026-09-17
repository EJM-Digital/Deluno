using System.Runtime.CompilerServices;
using Deluno.Filesystem;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
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
/// A finished download outlives the title it was grabbed for. On the rig, a
/// minute after two proof films and their library were removed, their finished
/// torrents were imported into Movies as new titles named after the releases -
/// "Dementia 13 1963 1080p WEB DL DDP5 1 H 264 DELUNO" - because the planner
/// fell back to the first movie library and the import had no catalogue entry to
/// name them from. Deluno now leaves such a download alone and says so, once.
/// </summary>
public sealed class RemovedTitleDownloadTests : IDisposable
{
    private const string ReleaseName = "Dementia.13.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO";

    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"deluno-removed-title-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public async Task A_download_whose_film_was_removed_is_not_imported_and_is_reported_once()
    {
        var scheduler = new Mock<IJobScheduler>();
        var activity = Activity();

        await Run(scheduler, activity, movieStillThere: false, grabbedFor: "library-1");

        scheduler.Verify(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        activity.Verify(instance => instance.RecordActivityAsync(
            WorkPlanner.NotImportedCategory,
            It.Is<string>(message => message.Contains("the film was removed from Deluno", StringComparison.Ordinal)),
            It.IsAny<string?>(), null, "download-dispatch", "dispatch-1", It.IsAny<CancellationToken>()), Times.Once);

        // The next pass finds it already said.
        var toldBefore = (ActivityEventItem)RuntimeHelpers.GetUninitializedObject(typeof(ActivityEventItem)) with { Category = WorkPlanner.NotImportedCategory };
        var again = Activity();
        again.Setup(instance => instance.ListActivityAsync(20, "download-dispatch", "dispatch-1", It.IsAny<CancellationToken>())).ReturnsAsync([toldBefore]);

        await Run(scheduler, again, movieStillThere: false, grabbedFor: "library-1");

        again.Verify(instance => instance.RecordActivityAsync(WorkPlanner.NotImportedCategory, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        scheduler.Verify(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_download_whose_library_was_removed_is_not_imported_into_another()
    {
        var scheduler = new Mock<IJobScheduler>();
        var activity = Activity();

        await Run(scheduler, activity, movieStillThere: true, grabbedFor: "removed-library");

        scheduler.Verify(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        activity.Verify(instance => instance.RecordActivityAsync(
            WorkPlanner.NotImportedCategory,
            It.Is<string>(message => message.Contains("the library that grabbed it was removed", StringComparison.Ordinal)),
            It.IsAny<string?>(), null, "download-dispatch", "dispatch-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_download_whose_film_is_still_there_is_imported()
    {
        var scheduler = new Mock<IJobScheduler>();
        scheduler.Setup(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobQueueItem)RuntimeHelpers.GetUninitializedObject(typeof(JobQueueItem)) with { Id = "import-job" });

        await Run(scheduler, Activity(), movieStillThere: true, grabbedFor: "library-1");

        scheduler.Verify(instance => instance.EnqueueAsync(
            It.Is<EnqueueJobRequest>(request => request.JobType == "filesystem.import.execute"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Mock<IActivityFeedRepository> Activity()
    {
        var activity = new Mock<IActivityFeedRepository>();
        activity.Setup(instance => instance.ListActivityAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ActivityEventItem>());
        return activity;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task Run(Mock<IJobScheduler> scheduler, Mock<IActivityFeedRepository> activity, bool movieStillThere, string grabbedFor)
    {
        var file = Path.Combine(_root, "Downloads", ReleaseName, ReleaseName + ".mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "finished download");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-1));

        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(repository => repository.TryClaimScheduledPassAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        jobs.Setup(repository => repository.ListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        jobs.Setup(repository => repository.FindRecentDispatchLinkAsync("client-1", ReleaseName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchCatalogueLink("dispatch-1", "movie", "movie-1", LibraryId: grabbedFor));

        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repository => repository.GetByIdAsync("movie-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(movieStillThere ? (MovieListItem)RuntimeHelpers.GetUninitializedObject(typeof(MovieListItem)) with { Id = "movie-1", Title = "Dementia 13", ReleaseYear = 1963 } : null);

        var telemetry = new Mock<IDownloadClientTelemetryService>();
        telemetry.Setup(service => service.GetOverviewAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Overview(new DownloadQueueItem(
            "queue-1", "client-1", "qBittorrent", "qbittorrent", "movies", "Dementia 13 1963 1080p WEB DL DDP5 1 H 264 DELUNO", ReleaseName,
            "deluno-movies", DownloadQueueStatuses.Completed, 1, 0, 0, 100, 100, 0, "Indexer", null,
            DateTimeOffset.UnixEpoch, file, "library-1")));

        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Library()]);

        await new WorkPlanner(new FailingLogger(), jobs.Object, new ConfigurationBuilder().Build(), TimeProvider.System)
            .PlanImportAutomationAsync(
                scheduler.Object,
                Mock.Of<IProcessorRepository>(repository => repository.ListProcessorHandoffsAsync(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>())
                    == Task.FromResult<IReadOnlyList<ProcessorHandoffItem>>(Array.Empty<ProcessorHandoffItem>())),
                libraries.Object,
                new LibraryAvailabilityService(Mock.Of<IActivityFeedRepository>(), TimeProvider.System, NullLogger<LibraryAvailabilityService>.Instance),
                telemetry.Object,
                Mock.Of<IProcessorConnectionService>(),
                activity.Object,
                movies.Object,
                Mock.Of<ISeriesCatalogRepository>(),
                TimeProvider.System,
                CancellationToken.None);
    }

    private LibraryItem Library()
    {
        var now = DateTimeOffset.UnixEpoch;
        return new LibraryItem(
            "library-1", "Movies", "movies", "main", _root, Path.Combine(_root, "Downloads"), null, null, null,
            true, true, "standard", null, null, 360, "block",
            true, true, true, 6, 6, 10, null, null, "active", false, null, null, now, now);
    }

    /// <summary>A scheduled pass only logs what it throws; a test should see it.</summary>
    private sealed class FailingLogger : Microsoft.Extensions.Logging.ILogger<WorkPlanner>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                throw new InvalidOperationException(formatter(state, exception), exception);
            }
        }
    }

    private static DownloadTelemetryOverview Overview(DownloadQueueItem item)
    {
        var summary = new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0);
        return new(
            summary,
            [new DownloadClientTelemetrySnapshot(
                "client-1", "qBittorrent", "qbittorrent", "http://127.0.0.1:8080", "healthy", null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, false, true, "password"),
                summary, [item], [], DateTimeOffset.UnixEpoch)],
            DateTimeOffset.UnixEpoch);
    }
}
