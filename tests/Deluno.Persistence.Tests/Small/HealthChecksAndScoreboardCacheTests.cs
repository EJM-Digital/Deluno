using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Worker.HealthChecks.Checks;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Small;

/// <summary>
/// #1268: the Deluno storage and dispatch poller health checks, and the scoreboard's sixty-second cache.
/// </summary>
public sealed class HealthChecksAndScoreboardCacheTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    // ---- SystemDiskSpaceHealthCheck --------------------------------------------------------

    private static SystemDiskSpaceHealthCheck DiskCheck(string dataRoot, SystemDiskSpaceHealthCheck.DriveReader? reader = null)
        => new(Options.Create(new StoragePathOptions { DataRoot = dataRoot }), reader);

    private static async Task<HealthCheckOutcome> RunAsync(SystemDiskSpaceHealthCheck check)
        => Assert.Single(await check.RunAsync(Token));

    [Fact]
    public async Task A_drive_with_plenty_free_is_ok_and_says_how_much()
    {
        var root = Path.Combine(Path.GetTempPath(), "deluno-disk-check");
        var outcome = await RunAsync(DiskCheck(root, _ => (true, 1000, 500)));

        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Equal("system:disk-space", outcome.Id);
        Assert.Equal(HealthCheckCategories.System, outcome.Category);
        Assert.Equal("Deluno's own storage", outcome.Name);
        Assert.Equal($"50% free under {Path.GetFullPath(root)}.", outcome.Message);
    }

    [Theory]
    [InlineData(120, true)]
    [InlineData(121, false)]
    public async Task A_drive_with_twelve_percent_free_or_less_is_a_problem_with_a_cause_and_a_fix(long free, bool isProblem)
    {
        var outcome = await RunAsync(DiskCheck(Path.GetTempPath(), _ => (true, 1000, free)));

        Assert.Equal(isProblem ? HealthCheckStatuses.Problem : HealthCheckStatuses.Ok, outcome.Status);
        if (isProblem)
        {
            Assert.StartsWith("Only 12% free remains under ", outcome.Message);
            Assert.Equal("The drive Deluno's own data lives on is running low on space.", outcome.Cause);
            Assert.Equal("Free up space on this drive, or move Deluno's data root to a larger one.", outcome.Fix);
        }
    }

    [Theory]
    [InlineData(false, 1000)]
    [InlineData(true, 0)]
    public async Task A_drive_that_is_not_ready_or_has_no_size_is_unknown(bool ready, long total)
    {
        var outcome = await RunAsync(DiskCheck(Path.GetTempPath(), _ => (ready, total, 0)));

        Assert.Equal(HealthCheckStatuses.Unknown, outcome.Status);
        Assert.StartsWith("Deluno could not read free space for ", outcome.Message);
    }

    [Fact]
    public async Task A_drive_that_cannot_be_read_is_unknown_with_the_reason()
    {
        var outcome = await RunAsync(DiskCheck(Path.GetTempPath(), _ => throw new IOException("device gone")));

        Assert.Equal(HealthCheckStatuses.Unknown, outcome.Status);
        Assert.EndsWith(": device gone", outcome.Message);
    }

    [Fact]
    public async Task A_data_root_that_is_not_a_path_is_unknown()
    {
        var outcome = await RunAsync(DiskCheck(""));

        Assert.Equal(HealthCheckStatuses.Unknown, outcome.Status);
        Assert.StartsWith("Deluno could not read free space for : ", outcome.Message);
    }

    [Fact]
    public async Task The_real_drive_under_the_data_root_is_read_when_none_is_given()
    {
        var outcome = await RunAsync(DiskCheck(Path.GetTempPath()));

        Assert.Contains(outcome.Status, new[] { HealthCheckStatuses.Ok, HealthCheckStatuses.Problem });
        Assert.Contains("free", outcome.Message);
    }

    // ---- DispatchRecoveryPollerHealthCheck -------------------------------------------------

    private static DispatchRecoveryPollerHealthCheck PollerCheck(bool open, DateTimeOffset? resumes = null, int failures = 0)
        => new(InterfaceStub.Create<IDownloadDispatchCircuitBreakerState>((name, _) => name switch
        {
            "get_IsOpen" => open,
            "get_ResumesAtUtc" => resumes,
            "get_ConsecutiveFailures" => failures,
            _ => throw new NotSupportedException(name),
        }));

    [Fact]
    public async Task A_closed_breaker_reads_running_normally()
    {
        var outcome = Assert.Single(await PollerCheck(open: false).RunAsync(Token));

        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Equal("system:dispatch-recovery-poller", outcome.Id);
        Assert.Equal("Running on its normal schedule.", outcome.Message);
    }

    [Fact]
    public async Task An_open_breaker_says_how_many_passes_failed_and_when_it_resumes()
    {
        var resumes = DateTimeOffset.Parse("2026-10-01T12:15:00Z");

        var outcome = Assert.Single(await PollerCheck(open: true, resumes, failures: 5).RunAsync(Token));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal(
            "Deluno paused checking downloads for grab, detection and import timeouts after 5 failed passes in a row. It resumes on its own at 2026-10-01 12:15:00Z.",
            outcome.Message);
        Assert.Equal("Repeated failures reading download state tripped a safety pause.", outcome.Cause);
        Assert.StartsWith("Check Deluno's logs", outcome.Fix);
    }

    [Fact]
    public async Task An_open_breaker_with_no_resume_time_omits_it()
    {
        var outcome = Assert.Single(await PollerCheck(open: true, resumes: null, failures: 3).RunAsync(Token));

        Assert.EndsWith("after 3 failed passes in a row.", outcome.Message);
        Assert.Equal(HealthCheckCategories.System, new DispatchRecoveryPollerHealthCheck(null!).Category);
    }

    // ---- IndexerScoreboardCache ------------------------------------------------------------

    [Fact]
    public async Task The_scoreboard_is_counted_once_per_window_until_it_ages_or_the_jobs_database_is_written()
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-01T12:00:00Z"));
        long generation = 1;
        var databases = InterfaceStub.Create<IDelunoDatabaseConnectionFactory>((name, args) =>
        {
            Assert.Equal("GetWriteGeneration", name);
            Assert.Equal(DelunoDatabaseNames.Jobs, args[0]);
            return generation;
        });
        var cache = new IndexerScoreboardCache(clock, databases);
        var computed = 0;
        Task<IndexerScoreboardSnapshot> Compute()
        {
            computed++;
            return Task.FromResult<IndexerScoreboardSnapshot>(null!);
        }

        await cache.GetAsync("30d", Compute, Token);
        await cache.GetAsync("30d", Compute, Token);
        Assert.Equal(1, computed);

        await cache.GetAsync("7d", Compute, Token);
        Assert.Equal(2, computed);

        generation = 2;
        await cache.GetAsync("30d", Compute, Token);
        Assert.Equal(3, computed);

        clock.Advance(IndexerScoreboardCache.Lifetime + TimeSpan.FromSeconds(1));
        await cache.GetAsync("30d", Compute, Token);
        Assert.Equal(4, computed);
        Assert.Equal(TimeSpan.FromSeconds(60), IndexerScoreboardCache.Lifetime);
    }
}
