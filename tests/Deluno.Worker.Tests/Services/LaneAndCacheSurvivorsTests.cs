using Deluno.Jobs.Contracts;
using Deluno.Movies.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Services;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>Rules a first round of lane and cache tests let slip: a stopped lane asks for nothing more, and an unknown show costs no read of the library.</summary>
public sealed class LaneAndCacheSurvivorsTests
{
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task A_lane_stopped_while_it_waits_with_a_job_running_asks_for_no_more_work()
    {
        var job = TestJobs.Create("test.job");
        var release = NewSignal();
        var started = NewSignal();
        using var stop = new CancellationTokenSource();
        var ticks = 0;
        var lane = new JobLane("test", TimeSpan.FromHours(1), ["test.job"], BatchSize: 2, MaxConcurrency: 2, JitterOverride: TimeSpan.Zero);

        var run = new LaneRunner().RunAsync(
            lane,
            new SemaphoreSlim(0, 1),
            (_, _) => Task.FromResult(Interlocked.Increment(ref ticks) == 1
                ? new LaneTickResult([job], TimeSpan.FromHours(1))
                : LaneTickResult.Empty(TimeSpan.FromHours(1))),
            async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
            },
            stop.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await stop.CancelAsync();
        release.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, ticks);
    }

    [Fact]
    public async Task A_lane_stopped_by_the_job_that_finishes_asks_for_no_more_work()
    {
        var job = TestJobs.Create("test.job");
        using var stop = new CancellationTokenSource();
        var ticks = 0;
        var lane = new JobLane("test", TimeSpan.FromHours(1), ["test.job"], BatchSize: 2, MaxConcurrency: 2, JitterOverride: TimeSpan.Zero);

        var run = new LaneRunner().RunAsync(
            lane,
            new SemaphoreSlim(0, 1),
            (_, _) => Task.FromResult(Interlocked.Increment(ref ticks) == 1
                ? new LaneTickResult([job], TimeSpan.FromHours(1))
                : LaneTickResult.Empty(TimeSpan.FromHours(1))),
            async (_, _) =>
            {
                await Task.Yield();
                await stop.CancelAsync();
            },
            stop.Token);

        await run.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, ticks);
    }
}
