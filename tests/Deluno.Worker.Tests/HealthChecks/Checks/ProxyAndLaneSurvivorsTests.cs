using Deluno.Connections.Contracts;
using Deluno.Worker.HealthChecks;
using Deluno.Worker.HealthChecks.Checks;
using Deluno.Worker.Services;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>Two rules a first round let slip: a proxy test stopped by a person is never read as a timeout, and a lane never leaves an unused wait behind to swallow the next wake.</summary>
public sealed class ProxyAndLaneSurvivorsTests
{
    [Fact]
    public async Task A_proxy_test_that_ran_out_of_time_and_was_stopped_by_a_person_is_stopped_not_timed_out()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HealthCheckHttpClients.Proxy)).Returns(new HttpClient());
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var proxy = new ProxyItem("p1", "Test proxy", ProxyTypes.Socks5, "socks5://127.0.0.1:1080", null, null, true, now, now);
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HttpProxyReachabilityTester(factory.Object, TimeSpan.Zero).TestAsync(proxy, stop.Token));
    }

    [Fact]
    public async Task A_wake_after_a_job_finished_wakes_the_lane_and_is_not_swallowed_by_an_earlier_wait()
    {
        var job = TestJobs.Create("test.job");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wake = new SemaphoreSlim(0, 1);
        using var stop = new CancellationTokenSource();
        var ticks = 0;
        var lane = new JobLane("test", TimeSpan.FromHours(1), ["test.job"], BatchSize: 2, MaxConcurrency: 2, JitterOverride: TimeSpan.Zero);

        var run = new LaneRunner().RunAsync(
            lane,
            wake,
            (_, _) => Task.FromResult(Interlocked.Increment(ref ticks) == 1
                ? new LaneTickResult([job], TimeSpan.FromHours(1))
                : LaneTickResult.Empty(TimeSpan.FromHours(1))),
            async (_, _) => await release.Task,
            stop.Token);

        await Deluno.Persistence.Tests.Support.Wait.UntilAsync(() => ticks == 1, "the first ask");
        release.TrySetResult();
        await Deluno.Persistence.Tests.Support.Wait.UntilAsync(() => ticks == 2, "the ask after the job finished");
        await Task.Delay(50); // reason: lets the lane reach its next wait; it has no signal for that
        wake.Release();
        await Deluno.Persistence.Tests.Support.Wait.UntilAsync(() => ticks == 3, "the ask after the wake");

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
    }
}
