using Deluno.Filesystem;
using Deluno.Integrations.DownloadClients;
using Deluno.Platform.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// A library Deluno cannot reach is paused, not worked on.
///
/// <para>DESIGN-007 decision 12. James, on a library root going away:
/// <i>"this isnt as bad as you think, thats where other mechanisms come into
/// play with a missing library being flagged as a system health issue which
/// would stop deluno doing anything at a library level"</i> — and then, given
/// the options, <b>"Flag it and pause that library"</b>.</para>
///
/// <para>The flag was built and the pause was not. An unmounted drive still had
/// every title in it searched for and every import attempted, and every one of
/// those failures was recorded against the release rather than against the
/// drive: one unplugged disk, a thousand failures, one cause.</para>
/// </summary>
public sealed class APausedLibraryTests
{
    [Fact]
    public async Task A_library_whose_root_is_gone_is_not_usable()
    {
        var availability = await Availability().ReadAsync(
            [Library("library-1", Path.Combine(Path.GetTempPath(), $"deluno-gone-{Guid.NewGuid():N}"))],
            CancellationToken.None);

        Assert.False(availability.IsUsable("library-1"));
    }

    [Fact]
    public async Task A_library_that_is_there_is_left_alone()
    {
        var availability = await Availability().ReadAsync(
            [Library("library-1", Path.GetTempPath())],
            CancellationToken.None);

        Assert.True(availability.IsUsable("library-1"));
        Assert.Empty(availability.UnreachableLibraryIds);
    }

    /// <summary>
    /// One outage must not take the others with it. A NAS going down should
    /// stop the libraries on the NAS, not the one on the internal disk.
    /// </summary>
    [Fact]
    public async Task One_library_going_does_not_pause_the_others()
    {
        var availability = await Availability().ReadAsync(
            [
                Library("on-the-nas", Path.Combine(Path.GetTempPath(), $"deluno-gone-{Guid.NewGuid():N}")),
                Library("on-the-disk", Path.GetTempPath())
            ],
            CancellationToken.None);

        Assert.False(availability.IsUsable("on-the-nas"));
        Assert.True(availability.IsUsable("on-the-disk"));
    }

    /// <summary>
    /// A library nobody has finished configuring is not an outage. Pausing it
    /// would be right, but saying "not reachable" about a path that was never
    /// set sends somebody to check a drive that was never involved.
    /// </summary>
    [Fact]
    public async Task A_library_with_no_root_configured_is_not_called_an_outage()
    {
        var activity = new Mock<IActivityFeedRepository>();

        var availability = await Availability(activity).ReadAsync(
            [Library("library-1", string.Empty)],
            CancellationToken.None);

        Assert.True(availability.IsUsable("library-1"));
        activity.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A pause nobody is told about is indistinguishable from Deluno having
    /// quietly stopped working — which is the complaint this design exists to
    /// answer.
    /// </summary>
    [Fact]
    public async Task Pausing_a_library_is_said_out_loud_once()
    {
        var activity = new Mock<IActivityFeedRepository>();
        var service = Availability(activity);
        var gone = Library("library-1", Path.Combine(Path.GetTempPath(), $"deluno-gone-{Guid.NewGuid():N}"));

        await service.ReadAsync([gone], CancellationToken.None);

        activity.Verify(
            feed => feed.RecordActivityAsync(
                "library.paused",
                It.Is<string>(message =>
                    message.Contains("paused", StringComparison.OrdinalIgnoreCase) &&
                    message.Contains("nothing has been changed", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                "library-1",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// And once is once. The worker asks on every tick; announcing on every
    /// tick would bury the activity feed under one outage.
    /// </summary>
    [Fact]
    public async Task A_library_that_is_still_gone_is_not_announced_again()
    {
        var activity = new Mock<IActivityFeedRepository>();
        var clock = new AdvanceableClock(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var service = Availability(activity, clock);
        var gone = Library("library-1", Path.Combine(Path.GetTempPath(), $"deluno-gone-{Guid.NewGuid():N}"));

        await service.ReadAsync([gone], CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        await service.ReadAsync([gone], CancellationToken.None);

        activity.Verify(
            feed => feed.RecordActivityAsync(
                "library.paused",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Coming back is worth saying too. Somebody who was told Deluno stopped
    /// should be told it started, without having to go and look.
    /// </summary>
    [Fact]
    public async Task A_library_that_comes_back_says_so()
    {
        var activity = new Mock<IActivityFeedRepository>();
        var clock = new AdvanceableClock(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var service = Availability(activity, clock);
        var root = Path.Combine(Path.GetTempPath(), $"deluno-flaky-{Guid.NewGuid():N}");

        await service.ReadAsync([Library("library-1", root)], CancellationToken.None);

        try
        {
            Directory.CreateDirectory(root);
            clock.Advance(TimeSpan.FromHours(1));
            var availability = await service.ReadAsync([Library("library-1", root)], CancellationToken.None);

            Assert.True(availability.IsUsable("library-1"));
            activity.Verify(
                feed => feed.RecordActivityAsync(
                    "library.resumed",
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    "library-1",
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The worker asks on every tick and a stat call per library per tick
    /// against a sleeping NAS is its own problem, so the answer is held
    /// briefly.
    /// </summary>
    [Fact]
    public async Task An_answer_given_a_moment_ago_is_reused()
    {
        var clock = new AdvanceableClock(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        var service = Availability(clock: clock);
        var root = Path.Combine(Path.GetTempPath(), $"deluno-cached-{Guid.NewGuid():N}");

        var first = await service.ReadAsync([Library("library-1", root)], CancellationToken.None);
        Assert.False(first.IsUsable("library-1"));

        try
        {
            // The path appears, but not enough time has passed for Deluno to
            // have looked again.
            Directory.CreateDirectory(root);
            clock.Advance(TimeSpan.FromSeconds(5));

            Assert.False((await service.ReadAsync([Library("library-1", root)], CancellationToken.None)).IsUsable("library-1"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The wiring, not just the answer. Import automation reads the library
    /// list and then does everything else; if the pause is not applied there,
    /// an unmounted drive still gets every finished download moved into it.
    /// </summary>
    [Fact]
    public async Task Import_automation_does_no_work_for_a_library_that_is_paused()
    {
        var jobs = ClaimingJobs();
        var gone = Library("library-1", Path.Combine(Path.GetTempPath(), $"deluno-gone-{Guid.NewGuid():N}"));

        await Planner(jobs).PlanImportAutomationAsync(
            Mock.Of<IJobScheduler>(),
            Mock.Of<IProcessorRepository>(),
            LibrariesReturning(gone),
            Availability(),
            Mock.Of<IDownloadClientTelemetryService>(),
            Mock.Of<IProcessorConnectionService>(),
            Mock.Of<IActivityFeedRepository>(),
            Mock.Of<IMovieCatalogRepository>(),
            Mock.Of<ISeriesCatalogRepository>(),
            TimeProvider.System,
            CancellationToken.None);

        // Reading the job queue is the first thing it does once it has decided
        // there is a library worth working on.
        jobs.Verify(
            repository => repository.ListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Import_automation_carries_on_for_a_library_that_is_there()
    {
        var jobs = ClaimingJobs();

        await Planner(jobs).PlanImportAutomationAsync(
            Mock.Of<IJobScheduler>(),
            Mock.Of<IProcessorRepository>(),
            LibrariesReturning(Library("library-1", Path.GetTempPath())),
            Availability(),
            Mock.Of<IDownloadClientTelemetryService>(),
            Mock.Of<IProcessorConnectionService>(),
            Mock.Of<IActivityFeedRepository>(),
            Mock.Of<IMovieCatalogRepository>(),
            Mock.Of<ISeriesCatalogRepository>(),
            TimeProvider.System,
            CancellationToken.None);

        jobs.Verify(
            repository => repository.ListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// Search planning is gated too, and it is gated in the worker rather than
    /// in a service anybody can test, so this reads the source.
    ///
    /// <para>The failure it catches is somebody removing the filter while
    /// leaving everything else working: the pause would still be announced and
    /// imports would still stop, and the library would quietly go on being
    /// searched for titles nobody could import. A silent half-pause is worse
    /// than none, because the activity feed would say it was paused.</para>
    /// </summary>
    [Fact]
    public void Search_planning_is_gated_on_the_same_answer()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Deluno.Worker", "Services", "DelunoHeartbeatWorker.cs"));

        var plans = source.IndexOf("var automationPlans = libraries", StringComparison.Ordinal);
        Assert.True(plans >= 0, "The worker no longer builds automation plans from the library list.");

        var projection = source[plans..source.IndexOf("PlanLibrarySearchesAsync", plans, StringComparison.Ordinal)];

        Assert.Contains("IsUsable", projection, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    // ------------------------------------------------------------------ helpers

    private static Mock<IJobQueueRepository> ClaimingJobs()
    {
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(repository => repository.TryClaimScheduledPassAsync(
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        jobs.Setup(repository => repository.ListAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return jobs;
    }

    private static WorkPlanner Planner(Mock<IJobQueueRepository> jobs)
        => new(
            NullLogger<WorkPlanner>.Instance,
            jobs.Object,
            new ConfigurationBuilder().Build(),
            TimeProvider.System);

    private static ILibrariesRepository LibrariesReturning(params LibraryItem[] libraries)
    {
        var repository = new Mock<ILibrariesRepository>();
        repository.Setup(instance => instance.ListLibrariesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(libraries);
        return repository.Object;
    }

    private static LibraryAvailabilityService Availability(
        Mock<IActivityFeedRepository>? activity = null,
        TimeProvider? clock = null)
        => new(
            (activity ?? new Mock<IActivityFeedRepository>()).Object,
            clock ?? new AdvanceableClock(DateTimeOffset.Parse("2026-09-05T12:00:00Z")),
            NullLogger<LibraryAvailabilityService>.Instance);

    private static LibraryItem Library(string id, string rootPath)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new LibraryItem(
            id, $"Library {id}", "movies", "main", rootPath, null, null, null, null,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
    }

    private sealed class AdvanceableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
