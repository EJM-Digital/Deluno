using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Recovery.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// Besides archiving stale dispatches, the clean-up pass drops indexer query telemetry older than 30 days and completed
/// jobs older than a year, and a failure in either never stops the pass (coverage register, area engines).
/// </summary>
public sealed class TheDispatchCleanupPassPrunesOldHistoryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-29T03:00:00Z");

    private static Mock<IDownloadDispatchesRepository> NoStaleDispatches()
    {
        var dispatches = new Mock<IDownloadDispatchesRepository>();
        dispatches.Setup(repository => repository.FindStaleFailedDispatchesAsync(It.IsAny<TimeSpan>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return dispatches;
    }

    [Fact]
    public async Task Telemetry_older_than_thirty_days_and_completed_jobs_older_than_a_year_are_pruned()
    {
        var stats = new Mock<IIndexerQueryStatsRepository>();
        var jobs = new Mock<IJobQueueRepository>();
        var service = new DispatchCleanupService(NoStaleDispatches().Object, new FakeClock(Now), NullLogger<DispatchCleanupService>.Instance, stats.Object, jobs.Object);

        var result = await service.RunCleanupPassAsync(CancellationToken.None);

        Assert.Equal(0, result.ArchivedCount);
        stats.Verify(repository => repository.PruneAsync(Now.AddDays(-30), It.IsAny<CancellationToken>()), Times.Once);
        jobs.Verify(repository => repository.PruneCompletedAsync(Now.AddDays(-365), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_failing_telemetry_prune_does_not_stop_the_job_prune_or_the_pass_and_the_other_way_round()
    {
        var failingStats = new Mock<IIndexerQueryStatsRepository>();
        failingStats.Setup(repository => repository.PruneAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("locked"));
        var goodJobs = new Mock<IJobQueueRepository>();
        await new DispatchCleanupService(NoStaleDispatches().Object, new FakeClock(Now), NullLogger<DispatchCleanupService>.Instance, failingStats.Object, goodJobs.Object)
            .RunCleanupPassAsync(CancellationToken.None);
        goodJobs.Verify(repository => repository.PruneCompletedAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);

        var goodStats = new Mock<IIndexerQueryStatsRepository>();
        var failingJobs = new Mock<IJobQueueRepository>();
        failingJobs.Setup(repository => repository.PruneCompletedAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("locked"));
        var result = await new DispatchCleanupService(NoStaleDispatches().Object, new FakeClock(Now), NullLogger<DispatchCleanupService>.Instance, goodStats.Object, failingJobs.Object)
            .RunCleanupPassAsync(CancellationToken.None);
        goodStats.Verify(repository => repository.PruneAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("No stale dispatches found.", result.Summary);
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
