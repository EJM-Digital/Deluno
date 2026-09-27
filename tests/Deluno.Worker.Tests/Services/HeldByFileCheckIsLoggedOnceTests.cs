using System.Runtime.CompilerServices;
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
/// #912: the 20-second file-stability check is a safety net behind a
/// download client's own settled status, not the primary signal — and when
/// it is what is actually holding an import back, that has to be visible
/// rather than a silent retry on every poll.
/// </summary>
public sealed class HeldByFileCheckIsLoggedOnceTests : IDisposable
{
    private const string ReleaseName = "Dementia.13.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO";

    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"deluno-held-by-file-check-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public async Task An_unsettled_file_is_not_imported_and_is_reported_once()
    {
        var scheduler = new Mock<IJobScheduler>();
        var activity = Activity();

        await Run(scheduler, activity);

        scheduler.Verify(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        activity.Verify(instance => instance.RecordActivityAsync(
            WorkPlanner.HeldByFileCheckCategory,
            It.Is<string>(message => message.Contains("waiting for the file to stop changing size", StringComparison.Ordinal)),
            It.IsAny<string?>(), null, "download-source", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Already_told_this_pass_does_not_log_again()
    {
        var scheduler = new Mock<IJobScheduler>();
        var toldBefore = (ActivityEventItem)RuntimeHelpers.GetUninitializedObject(typeof(ActivityEventItem)) with { Category = WorkPlanner.HeldByFileCheckCategory };
        var activity = Activity();
        activity.Setup(instance => instance.ListActivityAsync(5, "download-source", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([toldBefore]);

        await Run(scheduler, activity);

        activity.Verify(instance => instance.RecordActivityAsync(WorkPlanner.HeldByFileCheckCategory, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        scheduler.Verify(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
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

    private async Task Run(Mock<IJobScheduler> scheduler, Mock<IActivityFeedRepository> activity)
    {
        var file = Path.Combine(_root, "Downloads", ReleaseName, ReleaseName + ".mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "still being written");
        // Left at its natural (just-now) write time so ImportFileReadiness
        // sees a file younger than its minimum stable age — still arriving,
        // not settled — regardless of what the client itself reports.

        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(repository => repository.TryClaimScheduledPassAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        jobs.Setup(repository => repository.ListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

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
                Mock.Of<IProcessorRepository>(repository => repository.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())
                    == Task.FromResult<IReadOnlyList<ProcessorHandoffItem>>(Array.Empty<ProcessorHandoffItem>())),
                libraries.Object,
                new LibraryAvailabilityService(Mock.Of<IActivityFeedRepository>(), TimeProvider.System, NullLogger<LibraryAvailabilityService>.Instance),
                telemetry.Object,
                Mock.Of<IProcessorConnectionService>(),
                activity.Object,
                Mock.Of<IMovieCatalogRepository>(),
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
