using System.Runtime.CompilerServices;
using Deluno.Contracts;
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
using Deluno.Quality.Data;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// Checkpoint A (#917): wired into the same completed-download loop that hands
/// a finished download to a processor or imports it directly. Off by default
/// for every install (#936) -- these tests confirm both that a failing
/// download never reaches a processor or the library once the setting is on,
/// and that nothing changes at all while it is off.
/// </summary>
public sealed class DownloadIntegrityCheckpointTests : IDisposable
{
    private const string ReleaseName = "Sintel.2010.2160p.WEB-DL.DDP5.1.H.264-DELUNO";

    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"deluno-integrity-checkpoint-{Guid.NewGuid():N}")).FullName;

    private string _linkEntityType = "movie";
    private string _linkEntityId = "movie-1";

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Off_by_default_does_not_touch_the_download_or_stop_the_import()
    {
        var scheduler = new Mock<IJobScheduler>();
        scheduler.Setup(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobQueueItem)RuntimeHelpers.GetUninitializedObject(typeof(JobQueueItem)) with { Id = "import-job" });
        var telemetry = Telemetry(out var file);
        var blocked = new Mock<IBlockedReleaseRepository>();

        await Run(scheduler, telemetry, blocked, checkEnabled: false, probe: Probe.Failed());

        blocked.Verify(instance => instance.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()), Times.Never);
        telemetry.Verify(instance => instance.ExecuteActionAsync(It.IsAny<string>(), It.IsAny<DownloadClientActionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        scheduler.Verify(instance => instance.EnqueueAsync(
            It.Is<EnqueueJobRequest>(request => request.JobType == "filesystem.import.execute"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_failed_check_blocklists_removes_and_searches_again_without_importing()
    {
        var scheduler = new Mock<IJobScheduler>();
        var telemetry = Telemetry(out var file);
        telemetry.Setup(instance => instance.ExecuteActionAsync(
                "client-1",
                It.Is<DownloadClientActionRequest>(request => request.Action == DownloadClientActions.DeleteWithData && request.QueueItemId == "queue-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadClientActionResult("client-1", "queue-1", DownloadClientActions.DeleteWithData, true, "removed"));
        var blocked = new Mock<IBlockedReleaseRepository>();
        var activity = Activity();

        await Run(scheduler, telemetry, blocked, checkEnabled: true, probe: Probe.Failed(), activity: activity);

        blocked.Verify(instance => instance.BlockAsync(
            It.Is<BlockedRelease>(release => release.ReasonCode == ImportFailurePolicy.DownloadIntegrityFailed && release.ReleaseName == ReleaseName),
            It.IsAny<CancellationToken>()), Times.Once);
        telemetry.Verify(instance => instance.ExecuteActionAsync(
            "client-1",
            It.Is<DownloadClientActionRequest>(request => request.Action == DownloadClientActions.DeleteWithData),
            It.IsAny<CancellationToken>()), Times.Once);
        scheduler.Verify(instance => instance.EnqueueAsync(
            It.Is<EnqueueJobRequest>(request => request.JobType == LibrarySearchJobTypes.Movies),
            It.IsAny<CancellationToken>()), Times.Once);
        scheduler.Verify(instance => instance.EnqueueAsync(
            It.Is<EnqueueJobRequest>(request => request.JobType == "filesystem.import.execute"),
            It.IsAny<CancellationToken>()), Times.Never);
        activity.Verify(instance => instance.RecordActivityAsync(
            WorkPlanner.DownloadIntegrityFailedCategory,
            It.Is<string>(message => message.Contains("No video stream", StringComparison.Ordinal)),
            It.IsAny<string?>(), null, "movie", "movie-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>#1148: a removal the client refused is said as it was, never claimed.</summary>
    [Fact]
    public async Task A_removal_the_client_refused_is_named_with_its_reason_and_not_claimed()
    {
        var scheduler = new Mock<IJobScheduler>();
        var telemetry = Telemetry(out var file);
        telemetry.Setup(instance => instance.ExecuteActionAsync(
                "client-1",
                It.IsAny<DownloadClientActionRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadClientActionResult("client-1", "queue-1", DownloadClientActions.DeleteWithData, false, "Deluge refused the request: Torrent not in session"));
        var blocked = new Mock<IBlockedReleaseRepository>();
        var activity = Activity();

        await Run(scheduler, telemetry, blocked, checkEnabled: true, probe: Probe.Failed(), activity: activity);

        activity.Verify(instance => instance.RecordActivityAsync(
            WorkPlanner.DownloadIntegrityFailedCategory,
            It.Is<string>(message =>
                message.Contains("could not remove it from", StringComparison.Ordinal)
                && message.Contains("Torrent not in session", StringComparison.Ordinal)
                && !message.Contains("Deluno removed it", StringComparison.Ordinal)),
            It.IsAny<string?>(), null, "movie", "movie-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>#1148: the same failed download, seen again on a later pass, is not written to Activity again.</summary>
    [Fact]
    public async Task A_failure_already_reported_for_this_download_is_not_written_again()
    {
        var scheduler = new Mock<IJobScheduler>();
        var telemetry = Telemetry(out var file);
        var blocked = new Mock<IBlockedReleaseRepository>();
        var activity = Activity();
        activity.Setup(instance => instance.ListActivityAsync(It.IsAny<int>(), "movie", "movie-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ActivityEventItem(
                "event-1", WorkPlanner.DownloadIntegrityFailedCategory, "earlier", "{\"itemId\":\"queue-1\",\"removedFromClient\":true}",
                null, "movie", "movie-1", DateTimeOffset.UtcNow.AddMinutes(-1))]);

        await Run(scheduler, telemetry, blocked, checkEnabled: true, probe: Probe.Failed(), activity: activity);

        activity.Verify(instance => instance.RecordActivityAsync(
            WorkPlanner.DownloadIntegrityFailedCategory, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        blocked.Verify(instance => instance.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()), Times.Never);
        telemetry.Verify(instance => instance.ExecuteActionAsync(It.IsAny<string>(), It.IsAny<DownloadClientActionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_failed_check_on_a_release_chosen_on_search_removes_it_but_blocklists_and_searches_for_nothing()
    {
        _linkEntityType = "release";
        _linkEntityId = "0a1b2c3d4e5f";
        var scheduler = new Mock<IJobScheduler>();
        var telemetry = Telemetry(out var file);
        telemetry.Setup(instance => instance.ExecuteActionAsync(
                "client-1",
                It.IsAny<DownloadClientActionRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadClientActionResult("client-1", "queue-1", DownloadClientActions.DeleteWithData, true, "removed"));
        var blocked = new Mock<IBlockedReleaseRepository>();
        var activity = Activity();

        await Run(scheduler, telemetry, blocked, checkEnabled: true, probe: Probe.Failed(), activity: activity);

        blocked.Verify(instance => instance.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()), Times.Never);
        scheduler.Verify(instance => instance.EnqueueAsync(
            It.Is<EnqueueJobRequest>(request => request.JobType == LibrarySearchJobTypes.Movies),
            It.IsAny<CancellationToken>()), Times.Never);
        activity.Verify(instance => instance.RecordActivityAsync(
            WorkPlanner.DownloadIntegrityFailedCategory,
            It.Is<string>(message => message.Contains("choose one on Search again", StringComparison.Ordinal)),
            It.IsAny<string?>(), null, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_passed_check_still_imports_and_is_recorded_on_the_titles_history()
    {
        var scheduler = new Mock<IJobScheduler>();
        scheduler.Setup(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobQueueItem)RuntimeHelpers.GetUninitializedObject(typeof(JobQueueItem)) with { Id = "import-job" });
        var telemetry = Telemetry(out var file);
        var blocked = new Mock<IBlockedReleaseRepository>();
        var activity = Activity();

        await Run(scheduler, telemetry, blocked, checkEnabled: true, probe: Probe.Ok(), activity: activity);

        blocked.Verify(instance => instance.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()), Times.Never);
        telemetry.Verify(instance => instance.ExecuteActionAsync(It.IsAny<string>(), It.IsAny<DownloadClientActionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        scheduler.Verify(instance => instance.EnqueueAsync(
            It.Is<EnqueueJobRequest>(request => request.JobType == "filesystem.import.execute"),
            It.IsAny<CancellationToken>()), Times.Once);
        activity.Verify(instance => instance.RecordActivityAsync(
            WorkPlanner.DownloadIntegrityPassedCategory,
            It.IsAny<string>(),
            It.IsAny<string?>(), null, "movie", "movie-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>#1155: a file that passed is not checked, or written about, again on the next poll while it is unchanged.</summary>
    [Fact]
    public async Task A_file_that_passed_is_checked_and_recorded_once_across_polls()
    {
        var scheduler = new Mock<IJobScheduler>();
        scheduler.Setup(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobQueueItem)RuntimeHelpers.GetUninitializedObject(typeof(JobQueueItem)) with { Id = "import-job" });
        var telemetry = Telemetry(out var file);
        var blocked = new Mock<IBlockedReleaseRepository>();
        var activity = Activity();
        var probes = 0;

        for (var poll = 0; poll < 2; poll++)
        {
            await RunWithProbe(scheduler, telemetry, blocked, checkEnabled: true, (_, _) =>
            {
                probes++;
                return Task.FromResult(Probe.Ok().Info);
            }, activity);
        }

        Assert.Equal(1, probes);
        activity.Verify(instance => instance.RecordActivityAsync(
            WorkPlanner.DownloadIntegrityPassedCategory,
            It.IsAny<string>(),
            It.IsAny<string?>(), null, "movie", "movie-1", It.IsAny<CancellationToken>()), Times.Once);

        // Changed on disk, it is checked afresh.
        File.AppendAllText(file, "more");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-1));
        await RunWithProbe(scheduler, telemetry, blocked, checkEnabled: true, (_, _) => Task.FromResult(Probe.Ok().Info), activity);
        activity.Verify(instance => instance.RecordActivityAsync(
            WorkPlanner.DownloadIntegrityPassedCategory,
            It.IsAny<string>(),
            It.IsAny<string?>(), null, "movie", "movie-1", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    /// <summary>
    /// #917: the check runs the same way whether the library hands its
    /// downloads to a processor first or imports them directly -- a failure
    /// stops either path before the processor ever sees the file.
    /// </summary>
    [Fact]
    public async Task A_failed_check_on_a_processor_library_never_reaches_the_processor()
    {
        var scheduler = new Mock<IJobScheduler>();
        var telemetry = Telemetry(out _);
        telemetry.Setup(instance => instance.ExecuteActionAsync(
                "client-1",
                It.Is<DownloadClientActionRequest>(request => request.Action == DownloadClientActions.DeleteWithData && request.QueueItemId == "queue-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadClientActionResult("client-1", "queue-1", DownloadClientActions.DeleteWithData, true, "removed"));
        var blocked = new Mock<IBlockedReleaseRepository>();
        var processorLibrary = Library(ImportWorkflows.RefineBeforeImport, "Weir");
        var processorRepository = new Mock<IProcessorRepository>(MockBehavior.Strict);
        processorRepository
            .Setup(repository => repository.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProcessorHandoffItem>());
processorRepository
            .Setup(repository => repository.ListDueProcessorOutcomesAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProcessorOutcomeRecord>());
processorRepository
            .Setup(repository => repository.FindProcessorHandoffForDownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessorHandoffItem?)null);
        processorRepository
            .Setup(repository => repository.ListProcessorHandoffsForReleaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProcessorHandoffItem>());
        processorRepository
            .Setup(repository => repository.FindProcessorHandoffAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessorHandoffItem?)null);

        await Run(scheduler, telemetry, blocked, checkEnabled: true, probe: Probe.Failed(), libraryOverride: processorLibrary, processorRepositoryOverride: processorRepository);

        blocked.Verify(instance => instance.BlockAsync(
            It.Is<BlockedRelease>(release => release.ReasonCode == ImportFailurePolicy.DownloadIntegrityFailed),
            It.IsAny<CancellationToken>()), Times.Once);
        telemetry.Verify(instance => instance.ExecuteActionAsync(
            "client-1",
            It.Is<DownloadClientActionRequest>(request => request.Action == DownloadClientActions.DeleteWithData),
            It.IsAny<CancellationToken>()), Times.Once);
        // Strict: any call to EnsureProcessorHandoffAsync (the hand-off itself)
        // would throw for being unconfigured, which is exactly the point --
        // the processor never sees a file Checkpoint A has already refused.
        processorRepository.Verify(repository => repository.EnsureProcessorHandoffAsync(It.IsAny<CreateProcessorHandoffRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A film in its own folder -- the common case -- is resolved to its own video file (#917).</summary>
    [Fact]
    public async Task A_movie_in_its_own_folder_is_resolved_and_checked()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "Downloads", ReleaseName)).FullName;
        var video = Path.Combine(folder, ReleaseName + ".mkv");
        File.WriteAllText(video, "finished download");
        File.SetLastWriteTimeUtc(video, DateTime.UtcNow.AddHours(-1));
        File.WriteAllText(Path.Combine(folder, ReleaseName + ".nfo"), "nfo");
        var sampleFolder = Directory.CreateDirectory(Path.Combine(folder, "Sample")).FullName;
        var sample = Path.Combine(sampleFolder, "sample.mkv");
        File.WriteAllText(sample, "sample");
        File.SetLastWriteTimeUtc(sample, DateTime.UtcNow.AddHours(-1));

        var scheduler = new Mock<IJobScheduler>();
        var telemetry = TelemetryWithSource(folder);
        telemetry.Setup(instance => instance.ExecuteActionAsync(
                "client-1", It.IsAny<DownloadClientActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadClientActionResult("client-1", "queue-1", DownloadClientActions.DeleteWithData, true, "removed"));
        var blocked = new Mock<IBlockedReleaseRepository>();
        var seenPaths = new List<string>();

        await RunWithProbe(scheduler, telemetry, blocked, checkEnabled: true, (path, _) =>
        {
            seenPaths.Add(path);
            return Task.FromResult(Probe.Failed().Info);
        });

        // Only the real film's own file was opened -- the sample never was.
        Assert.Equal(video, Assert.Single(seenPaths));
        blocked.Verify(instance => instance.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>A season pack checks every episode file, not only the largest (#917).</summary>
    [Fact]
    public async Task A_season_pack_folder_checks_every_episode_file()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "Downloads", ReleaseName)).FullName;
        var episode1 = Path.Combine(folder, "S01E01.mkv");
        var episode2 = Path.Combine(folder, "S01E02.mkv");
        File.WriteAllText(episode1, "episode one, bigger");
        File.WriteAllText(episode2, "e2");
        foreach (var file in new[] { episode1, episode2 })
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-1));
        }

        var scheduler = new Mock<IJobScheduler>();
        var telemetry = TelemetryWithSource(folder);
        telemetry.Setup(instance => instance.ExecuteActionAsync(
                "client-1", It.IsAny<DownloadClientActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadClientActionResult("client-1", "queue-1", DownloadClientActions.DeleteWithData, true, "removed"));
        var blocked = new Mock<IBlockedReleaseRepository>();

        // The larger file (checked first) passes; the smaller episode fails --
        // proving the pack is not judged solely on its biggest file.
        await RunWithProbe(scheduler, telemetry, blocked, checkEnabled: true, (path, _) =>
            Task.FromResult(string.Equals(path, episode2, StringComparison.OrdinalIgnoreCase) ? Probe.Failed().Info : Probe.Ok().Info));

        blocked.Verify(instance => instance.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>"Not the film the title says", on strong evidence: the file's own embedded title names a different film (#917).</summary>
    [Fact]
    public async Task An_embedded_title_naming_a_different_film_fails()
    {
        var scheduler = new Mock<IJobScheduler>();
        var telemetry = Telemetry(out _);
        telemetry.Setup(instance => instance.ExecuteActionAsync(
                "client-1", It.IsAny<DownloadClientActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadClientActionResult("client-1", "queue-1", DownloadClientActions.DeleteWithData, true, "removed"));
        var blocked = new Mock<IBlockedReleaseRepository>();
        var activity = Activity();

        await Run(scheduler, telemetry, blocked, checkEnabled: true, probe: Probe.WithTitle("Some Completely Different Movie"), activity: activity);

        blocked.Verify(instance => instance.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()), Times.Once);
        activity.Verify(instance => instance.RecordActivityAsync(
            WorkPlanner.DownloadIntegrityFailedCategory,
            It.Is<string>(message => message.Contains("title says", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<string?>(), null, "movie", "movie-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Weak evidence -- an embedded title that overlaps with the known one -- never fails a file (#917).</summary>
    [Fact]
    public async Task An_embedded_title_that_overlaps_the_known_title_is_weak_evidence_and_passes()
    {
        var scheduler = new Mock<IJobScheduler>();
        scheduler.Setup(instance => instance.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobQueueItem)RuntimeHelpers.GetUninitializedObject(typeof(JobQueueItem)) with { Id = "import-job" });
        var telemetry = Telemetry(out _);
        var blocked = new Mock<IBlockedReleaseRepository>();

        // "Sintel" is known; "Sintel Extended Cut" merely extends it.
        await Run(scheduler, telemetry, blocked, checkEnabled: true, probe: Probe.WithTitle("Sintel Extended Cut"));

        blocked.Verify(instance => instance.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Mock<IActivityFeedRepository> Activity()
    {
        var activity = new Mock<IActivityFeedRepository>();
        activity.Setup(instance => instance.ListActivityAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ActivityEventItem>());
        return activity;
    }

    private Mock<IDownloadClientTelemetryService> Telemetry(out string file)
    {
        file = Path.Combine(_root, "Downloads", ReleaseName, ReleaseName + ".mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "finished download");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-1));
        return TelemetryWithSource(file);
    }

    private static Mock<IDownloadClientTelemetryService> TelemetryWithSource(string sourcePath)
    {
        var summary = new DownloadTelemetrySummary(0, 0, 0, 0, 0, 0, 0);
        var item = new DownloadQueueItem(
            "queue-1", "client-1", "qBittorrent", "qbittorrent", "movies", "Sintel", ReleaseName,
            "deluno-movies", DownloadQueueStatuses.Completed, 1, 0, 0, 100, 100, 0, "Indexer", null,
            DateTimeOffset.UnixEpoch, sourcePath, "library-1");
        var overview = new DownloadTelemetryOverview(
            summary,
            [new DownloadClientTelemetrySnapshot(
                "client-1", "qBittorrent", "qbittorrent", "http://127.0.0.1:8080", "healthy", null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, false, true, "password"),
                summary, [item], [], DateTimeOffset.UnixEpoch)],
            DateTimeOffset.UnixEpoch);

        var telemetry = new Mock<IDownloadClientTelemetryService> { CallBase = true };
        telemetry.Setup(service => service.GetOverviewAsync(It.IsAny<CancellationToken>())).ReturnsAsync(overview);
        return telemetry;
    }

    private async Task Run(
        Mock<IJobScheduler> scheduler,
        Mock<IDownloadClientTelemetryService> telemetry,
        Mock<IBlockedReleaseRepository> blocked,
        bool checkEnabled,
        Probe probe,
        Mock<IActivityFeedRepository>? activity = null,
        LibraryItem? libraryOverride = null,
        Mock<IProcessorRepository>? processorRepositoryOverride = null)
        => await RunWithProbe(
            scheduler, telemetry, blocked, checkEnabled,
            (_, _) => Task.FromResult(probe.Info),
            activity, libraryOverride, processorRepositoryOverride);

    /// <summary>Like <see cref="Run"/>, but lets a test answer differently per file path -- needed to prove a season pack's files are each checked (#917).</summary>
    private async Task RunWithProbe(
        Mock<IJobScheduler> scheduler,
        Mock<IDownloadClientTelemetryService> telemetry,
        Mock<IBlockedReleaseRepository> blocked,
        bool checkEnabled,
        Func<string, CancellationToken, Task<MediaProbeInfo>> probeByPath,
        Mock<IActivityFeedRepository>? activity = null,
        LibraryItem? libraryOverride = null,
        Mock<IProcessorRepository>? processorRepositoryOverride = null)
    {
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(repository => repository.TryClaimScheduledPassAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        jobs.Setup(repository => repository.ListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        jobs.Setup(repository => repository.FindDispatchLinkByClientItemAsync("client-1", "queue-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchCatalogueLink("dispatch-1", _linkEntityType, _linkEntityId, IndexerName: "Indexer", LibraryId: "library-1"));

        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([libraryOverride ?? Library()]);

        var movies = new Mock<IMovieCatalogRepository>();
        movies.Setup(repository => repository.GetByIdAsync("movie-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((MovieListItem)RuntimeHelpers.GetUninitializedObject(typeof(MovieListItem)) with { Id = "movie-1", Title = "Sintel", ReleaseYear = 2010 });

        var settings = new Mock<IPlatformSettingsRepository>();
        settings.Setup(repository => repository.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Settings(checkEnabled));

        var probeService = new Mock<IMediaProbeService>();
        probeService.Setup(service => service.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(probeByPath);

        var processorRepository = processorRepositoryOverride ?? NoProcessorHandoffs();

        await new WorkPlanner(new FailingLogger(), jobs.Object, new ConfigurationBuilder().Build(), TimeProvider.System)
            .PlanImportAutomationAsync(
                scheduler.Object,
                processorRepository.Object,
                libraries.Object,
                new LibraryAvailabilityService(Mock.Of<IActivityFeedRepository>(), TimeProvider.System, NullLogger<LibraryAvailabilityService>.Instance),
                telemetry.Object,
                Mock.Of<IProcessorConnectionService>(),
                (activity ?? Activity()).Object,
                movies.Object,
                Mock.Of<ISeriesCatalogRepository>(),
                TimeProvider.System,
                CancellationToken.None,
                settings.Object,
                probeService.Object,
                blocked.Object,
                Mock.Of<IQualityRepository>());
    }

    private static Mock<IProcessorRepository> NoProcessorHandoffs()
    {
        var processorRepository = new Mock<IProcessorRepository>();
        processorRepository
            .Setup(repository => repository.ListProcessorHandoffsWithStatusAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProcessorHandoffItem>());
processorRepository
            .Setup(repository => repository.FindProcessorHandoffForDownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessorHandoffItem?)null);
        processorRepository
            .Setup(repository => repository.ListProcessorHandoffsForReleaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProcessorHandoffItem>());
        return processorRepository;
    }

    private static PlatformSettingsSnapshot Settings(bool checkEnabled) => new(
        MovieRootPath: null,
        SeriesRootPath: null,
        DownloadsPath: null,
        IncompleteDownloadsPath: null,
        AutoStartJobs: true,
        EnableNotifications: true,
        RenameOnImport: true,
        UseHardlinks: true,
        CleanupEmptyFolders: true,
        MovieFolderFormat: "{Movie}",
        SeriesFolderFormat: "{Series}",
        MovieFileFormat: "{Movie}",
        EpisodeFileFormat: "{Episode}",
        HostBindAddress: "127.0.0.1",
        HostPort: 7979,
        UrlBase: string.Empty,
        RequireAuthentication: false,
        UiTheme: "dark",
        DefaultMovieView: "poster",
        DefaultShowView: "poster",
        MetadataCertificationCountry: "US",
        MetadataLanguage: "en",
        MetadataProviderMode: "auto",
        MetadataBrokerUrl: string.Empty,
        MetadataBrokerConfigured: false,
        MetadataTmdbApiKeyConfigured: false,
        MetadataOmdbApiKeyConfigured: false,
        ReleaseNeverGrabPatterns: string.Empty,
        SearchScoringMode: "balanced",
        ImportRecoveryRetentionDays: 30,
        UpdatedUtc: DateTimeOffset.UnixEpoch,
        LibraryFreeSpaceWarnPercent: 10,
        LibraryFreeSpacePausePercent: 2,
        DownloadIntegrityCheckEnabled: checkEnabled,
        DownloadIntegrityMinimumRuntimePercent: 80);

    private LibraryItem Library(string importWorkflow = "standard", string? processorName = null)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new LibraryItem(
            "library-1", "Movies", "movies", "main", _root, Path.Combine(_root, "Downloads"), null, null, null,
            true, true, importWorkflow, processorName, null, 360, "block",
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

    private sealed class Probe(MediaProbeInfo info)
    {
        public MediaProbeInfo Info => info;

        public static Probe Ok() => new(new MediaProbeInfo(
            "succeeded", "ffprobe", null, 120 * 60, "mkv", null,
            [new MediaVideoStreamInfo(0, "h264", null, 3840, 2160, "yuv420p", 24, null, null)],
            [new MediaAudioStreamInfo(1, "aac", null, 2, "stereo", 48000, null, "eng")],
            []));

        public static Probe Failed() => new(new MediaProbeInfo("succeeded", "ffprobe", null, 120 * 60, "mkv", null, [], [], []));

        public static Probe WithTitle(string embeddedTitle) => new(new MediaProbeInfo(
            "succeeded", "ffprobe", null, 120 * 60, "mkv", null,
            [new MediaVideoStreamInfo(0, "h264", null, 3840, 2160, "yuv420p", 24, null, null)],
            [new MediaAudioStreamInfo(1, "aac", null, 2, "stereo", 48000, null, "eng")],
            [],
            embeddedTitle));
    }
}
