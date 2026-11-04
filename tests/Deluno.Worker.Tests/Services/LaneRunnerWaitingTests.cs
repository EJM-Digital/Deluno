using System.Collections.Concurrent;
using Deluno.Jobs.Contracts;
using Deluno.Worker.Services;
using Deluno.Worker.Tests.Support;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// #1080: what wakes a lane: the clock when it is idle, a new job queued while one is running, or a job finishing; and what a
/// lane does when it is handed more work than it has room for.
/// </summary>
public sealed class LaneRunnerWaitingTests
{
    private static JobLane Lane(TimeSpan interval, int slots = 2) => new("test", interval, ["test.job"], BatchSize: slots, MaxConcurrency: slots, JitterOverride: TimeSpan.Zero);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task A_lane_handed_more_jobs_than_it_has_room_for_stops_with_a_message_naming_the_numbers()
    {
        var jobs = new[] { TestJobs.Create("test.job"), TestJobs.Create("test.job"), TestJobs.Create("test.job") };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new LaneRunner().RunAsync(
            Lane(TimeSpan.FromMilliseconds(20)),
            new SemaphoreSlim(0, 1),
            (_, _) => Task.FromResult(new LaneTickResult(jobs, TimeSpan.FromMilliseconds(20))),
            (_, _) => Task.CompletedTask,
            CancellationToken.None));

        Assert.Equal("Lane 'test' returned 3 jobs for 2 available slot(s).", exception.Message);
    }

    [Fact]
    public async Task An_idle_lane_asks_again_when_its_own_interval_runs_out()
    {
        var ticks = 0;
        var third = NewSignal();
        using var stop = new CancellationTokenSource();

        var run = new LaneRunner().RunAsync(
            Lane(TimeSpan.FromMilliseconds(10)),
            new SemaphoreSlim(0, 1),
            (_, _) =>
            {
                if (Interlocked.Increment(ref ticks) == 3) third.TrySetResult();
                return Task.FromResult(LaneTickResult.Empty(TimeSpan.FromMilliseconds(10)));
            },
            (_, _) => Task.CompletedTask,
            stop.Token);

        await third.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task An_idle_lane_asks_at_once_when_a_job_is_queued()
    {
        var ticks = 0;
        var second = NewSignal();
        var wake = new SemaphoreSlim(0, 1);
        using var stop = new CancellationTokenSource();

        var run = new LaneRunner().RunAsync(
            Lane(TimeSpan.FromHours(1)),
            wake,
            (_, _) =>
            {
                if (Interlocked.Increment(ref ticks) == 2) second.TrySetResult();
                return Task.FromResult(LaneTickResult.Empty(TimeSpan.FromHours(1)));
            },
            (_, _) => Task.CompletedTask,
            stop.Token);

        await Deluno.Persistence.Tests.Support.Wait.UntilAsync(() => ticks == 1, "the first ask");
        wake.Release();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task A_lane_with_a_job_running_asks_for_more_the_moment_a_job_is_queued()
    {
        var job = TestJobs.Create("test.job");
        var asks = new ConcurrentQueue<int>();
        var release = NewSignal();
        var second = NewSignal();
        var wake = new SemaphoreSlim(0, 1);
        using var stop = new CancellationTokenSource();
        var ticks = 0;

        var run = new LaneRunner().RunAsync(
            Lane(TimeSpan.FromHours(1)),
            wake,
            (slots, _) =>
            {
                asks.Enqueue(slots);
                var tick = Interlocked.Increment(ref ticks);
                if (tick == 2) second.TrySetResult();
                return Task.FromResult(tick == 1
                    ? new LaneTickResult([job], TimeSpan.FromHours(1))
                    : LaneTickResult.Empty(TimeSpan.FromHours(1)));
            },
            async (_, _) => await release.Task,
            stop.Token);

        await Deluno.Persistence.Tests.Support.Wait.UntilAsync(() => ticks == 1, "the first ask");
        wake.Release();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(3));
        release.TrySetResult();
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal([2, 1], asks.Take(2));
    }

    [Fact]
    public async Task A_lane_with_a_job_running_asks_for_more_the_moment_that_job_finishes()
    {
        var job = TestJobs.Create("test.job");
        var release = NewSignal();
        var second = NewSignal();
        using var stop = new CancellationTokenSource();
        var ticks = 0;

        var run = new LaneRunner().RunAsync(
            Lane(TimeSpan.FromHours(1)),
            new SemaphoreSlim(0, 1),
            (_, _) =>
            {
                var tick = Interlocked.Increment(ref ticks);
                if (tick == 2) second.TrySetResult();
                return Task.FromResult(tick == 1
                    ? new LaneTickResult([job], TimeSpan.FromHours(1))
                    : LaneTickResult.Empty(TimeSpan.FromHours(1)));
            },
            async (_, _) => await release.Task,
            stop.Token);

        await Deluno.Persistence.Tests.Support.Wait.UntilAsync(() => ticks == 1, "the first ask");
        Assert.False(second.Task.IsCompleted);
        release.TrySetResult();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Stopping_a_lane_that_waits_with_a_job_running_lets_the_job_finish_first()
    {
        var job = TestJobs.Create("test.job");
        var release = NewSignal();
        var started = NewSignal();
        using var stop = new CancellationTokenSource();
        var ticks = 0;

        var run = new LaneRunner().RunAsync(
            Lane(TimeSpan.FromHours(1)),
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
        await Task.Delay(50); // reason: shows the run has not returned while its job is still going; there is no event for an absence
        Assert.False(run.IsCompleted);
        release.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
    }
}
