using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Data;
using Deluno.Worker.HealthChecks.Checks;
using Microsoft.Extensions.Options;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// Deluno's own data drive and the dispatch poller's pause, on System > Health, for every answer a drive or the breaker can give,
/// with the drive read replaced so no real disk decides the outcome.
/// </summary>
public sealed class SystemDiskSpaceHealthCheckBranchesTests
{
    private static SystemDiskSpaceHealthCheck Check(SystemDiskSpaceHealthCheck.DriveReader reader, string dataRoot = "data")
        => new(Options.Create(new StoragePathOptions { DataRoot = dataRoot }), reader);

    [Theory]
    [InlineData(1000L, 120L, HealthCheckStatuses.Problem)]
    [InlineData(1000L, 10L, HealthCheckStatuses.Problem)]
    [InlineData(1000L, 121L, HealthCheckStatuses.Ok)]
    [InlineData(1000L, 900L, HealthCheckStatuses.Ok)]
    public async Task The_twelve_percent_floor_decides_between_ok_and_problem(long total, long free, string status)
    {
        var outcome = Assert.Single(await Check(_ => (true, total, free)).RunAsync(CancellationToken.None));
        Assert.Equal(status, outcome.Status);
        Assert.Equal("system:disk-space", outcome.Id);
        Assert.Equal(HealthCheckCategories.System, outcome.Category);
        if (status == HealthCheckStatuses.Problem)
        {
            Assert.Contains("free remains under", outcome.Message);
            Assert.Contains("Free up space on this drive", outcome.Fix);
        }
        else
        {
            Assert.Contains("free under", outcome.Message);
        }
    }

    [Theory]
    [InlineData(false, 1000L)]
    [InlineData(true, 0L)]
    [InlineData(true, -5L)]
    public async Task A_drive_that_is_not_ready_or_reports_no_size_is_unknown_not_a_problem(bool ready, long total)
    {
        var outcome = Assert.Single(await Check(_ => (ready, total, 0)).RunAsync(CancellationToken.None));
        Assert.Equal(HealthCheckStatuses.Unknown, outcome.Status);
        Assert.StartsWith("Deluno could not read free space for ", outcome.Message);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(ArgumentException))]
    public async Task A_drive_that_cannot_be_read_is_unknown_and_the_reason_is_shown(Type exceptionType)
    {
        var outcome = Assert.Single(await Check(_ => throw (Exception)Activator.CreateInstance(exceptionType, "no such drive")!, "somewhere").RunAsync(CancellationToken.None));
        Assert.Equal(HealthCheckStatuses.Unknown, outcome.Status);
        Assert.Equal("Deluno could not read free space for somewhere: no such drive", outcome.Message);
    }

    [Fact]
    public async Task A_data_root_that_is_not_a_path_at_all_is_unknown()
    {
        var outcome = Assert.Single(await Check(_ => (true, 1000, 500), "bad\0path").RunAsync(CancellationToken.None));
        Assert.Equal(HealthCheckStatuses.Unknown, outcome.Status);
    }

    [Fact]
    public async Task The_dispatch_poller_reads_ok_while_running_and_a_problem_while_paused()
    {
        var running = new Mock<IDownloadDispatchCircuitBreakerState>();
        running.SetupGet(s => s.IsOpen).Returns(false);
        var ok = Assert.Single(await new DispatchRecoveryPollerHealthCheck(running.Object).RunAsync(CancellationToken.None));
        Assert.Equal(HealthCheckStatuses.Ok, ok.Status);
        Assert.Equal("Running on its normal schedule.", ok.Message);

        var resumes = DateTimeOffset.Parse("2026-10-31T10:15:00Z");
        var paused = new Mock<IDownloadDispatchCircuitBreakerState>();
        paused.SetupGet(s => s.IsOpen).Returns(true);
        paused.SetupGet(s => s.ConsecutiveFailures).Returns(3);
        paused.SetupGet(s => s.ResumesAtUtc).Returns(resumes);
        var withTime = Assert.Single(await new DispatchRecoveryPollerHealthCheck(paused.Object).RunAsync(CancellationToken.None));
        Assert.Equal(HealthCheckStatuses.Problem, withTime.Status);
        Assert.Contains("after 3 failed passes in a row.", withTime.Message);
        Assert.Contains($"It resumes on its own at {resumes:u}.", withTime.Message);

        paused.SetupGet(s => s.ResumesAtUtc).Returns((DateTimeOffset?)null);
        var withoutTime = Assert.Single(await new DispatchRecoveryPollerHealthCheck(paused.Object).RunAsync(CancellationToken.None));
        Assert.DoesNotContain("resumes on its own at", withoutTime.Message);
    }
}
