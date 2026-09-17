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
/// A processor writes its result to a folder named after the release, so the
/// refined path is identical every time that release is fetched. The hand-off
/// reconciler skipped any output path an import job had ever touched, which
/// made that path a permanent reservation: a release could be refined and
/// imported exactly once, ever.
///
/// <para>On the lab rig this is what "the loop stops after refine" was. A
/// completed import from the previous day still held
/// <c>Refined\Movies\Big.Buck.Bunny.2008.1080p.WEB-DL.x264-DELUNO</c>, so the
/// hand-off for a fresh download sat at <c>waiting</c> with no candidates, and
/// nothing ever said why.</para>
///
/// <para><see cref="WorkPlanner.HasImportReservation"/> already scopes a
/// reservation to the dispatch that made it, and its own comment names this
/// exact failure. The rule existed; this call site did not use it. See
/// <see cref="RepeatedDispatchImportIdentityTests"/> for the rule itself.</para>
/// </summary>
public sealed class RefinedOutputImportedAgainTests : IDisposable
{
    private const string ReleaseName = "Big.Buck.Bunny.2008.1080p.WEB-DL.x264-DELUNO";

    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"deluno-refined-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public async Task A_release_refined_a_second_time_is_imported_a_second_time()
    {
        var scheduler = Scheduler();

        await Run(scheduler, completedImportBelongsTo: "dispatch-from-yesterday");

        scheduler.Verify(
            instance => instance.EnqueueAsync(
                It.Is<EnqueueJobRequest>(request => request.JobType == "filesystem.import.execute"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The dedupe the reservation was there for still has to hold, or the fix
    /// trades a stuck hand-off for the same file imported on every tick.
    /// </summary>
    [Fact]
    public async Task The_import_this_very_dispatch_already_queued_is_not_queued_again()
    {
        var scheduler = Scheduler();

        await Run(scheduler, completedImportBelongsTo: "dispatch-now");

        scheduler.Verify(
            instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task Run(Mock<IJobScheduler> scheduler, string completedImportBelongsTo)
    {
        var outputRoot = Path.Combine(_root, "Refined", "Movies");
        var refinedFile = Path.Combine(outputRoot, ReleaseName, ReleaseName + ".mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(refinedFile)!);
        await File.WriteAllTextAsync(refinedFile, "refined bytes");

        // Old enough to count as finished copying, without the test having to
        // wait for it to become so.
        File.SetLastWriteTimeUtc(refinedFile, DateTime.UtcNow.AddHours(-1));

        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(repository => repository.TryClaimScheduledPassAsync(
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        jobs.Setup(repository => repository.ListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([CompletedImportOf(refinedFile, completedImportBelongsTo)]);
        jobs.Setup(repository => repository.FindRecentDispatchLinkAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchCatalogueLink("dispatch-now", "movie", "movie-1"));

        var processors = new Mock<IProcessorRepository>();
        processors.Setup(repository => repository.ListProcessorHandoffsAsync(
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Waiting()]);

        var planner = new WorkPlanner(
            NullLogger<WorkPlanner>.Instance,
            jobs.Object,
            new ConfigurationBuilder().Build(),
            TimeProvider.System);

        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([RefineBeforeImportLibrary(outputRoot)]);

        await planner.PlanImportAutomationAsync(
            scheduler.Object,
            processors.Object,
            libraries.Object,
            new LibraryAvailabilityService(
                Mock.Of<IActivityFeedRepository>(),
                TimeProvider.System,
                NullLogger<LibraryAvailabilityService>.Instance),
            Mock.Of<IDownloadClientTelemetryService>(),
            Mock.Of<IProcessorConnectionService>(),
            Mock.Of<IActivityFeedRepository>(),
            // The film the release was grabbed for is still in Deluno: one that
            // was removed is not imported at all (RemovedTitleDownloadTests).
            Mock.Of<IMovieCatalogRepository>(repository => repository.GetByIdAsync("movie-1", It.IsAny<CancellationToken>())
                == Task.FromResult<Deluno.Movies.Contracts.MovieListItem?>((Deluno.Movies.Contracts.MovieListItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Deluno.Movies.Contracts.MovieListItem)))),
            Mock.Of<ISeriesCatalogRepository>(),
            TimeProvider.System,
            CancellationToken.None);
    }

    private static Mock<IJobScheduler> Scheduler()
    {
        var scheduler = new Mock<IJobScheduler>();
        scheduler.Setup(instance => instance.EnqueueAsync(
                It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Job("import-job", "queued", "{}"));
        return scheduler;
    }

    private ProcessorHandoffItem Waiting()
        => new(
            Id: "handoff-1",
            LibraryId: "library-1",
            MediaType: "movie",
            ClientId: "client-1",
            QueueItemId: "queue-1",
            ReleaseName: ReleaseName,
            SourcePath: Path.Combine(_root, "Downloads-Complete", "Movies", ReleaseName),
            ProcessorName: "Weir",
            Status: "waiting",
            OutputPath: null,
            ImportJobId: null,
            FailureMessage: null,
            CreatedUtc: DateTimeOffset.UnixEpoch,
            UpdatedUtc: DateTimeOffset.UnixEpoch);

    private static JobQueueItem CompletedImportOf(string sourcePath, string dispatchId)
        => Job(
            "import-" + dispatchId,
            "completed",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Preview = new { SourcePath = sourcePath },
                DispatchId = dispatchId
            }));

    private static JobQueueItem Job(string id, string status, string payloadJson)
        => new(
            Id: id,
            JobType: "filesystem.import.execute",
            Source: "processor-output-watch",
            Status: status,
            PayloadJson: payloadJson,
            Attempts: 1,
            CreatedUtc: DateTimeOffset.UnixEpoch,
            ScheduledUtc: DateTimeOffset.UnixEpoch,
            StartedUtc: null,
            CompletedUtc: null,
            LeasedUntilUtc: null,
            WorkerId: null,
            LastError: null,
            RelatedEntityType: "movie",
            RelatedEntityId: "movie-1",
            IdempotencyKey: null,
            DedupeKey: null,
            MaxAttempts: 3,
            LastAttemptUtc: null,
            NextAttemptUtc: null);

    private LibraryItem RefineBeforeImportLibrary(string outputRoot)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new LibraryItem(
            "library-1", "Movies", "movie", "main", _root, null, null, null, null,
            true, true, "refine-before-import", "Weir", outputRoot, 120, "block",
            true, true, true, 6, 6, 10, null, null, "active", false, null, null, now, now);
    }
}
