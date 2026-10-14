using System.Collections.Concurrent;
using Deluno.Worker.Services;
using Deluno.Worker.Tests.Support;

namespace Deluno.Worker.Tests.Services;

public sealed class LaneRunnerTests
{
    private static JobLane TestLane(int maxConcurrency = 2) => new(
        "test",
        TimeSpan.FromMilliseconds(20),
        ["test.job"],
        BatchSize: maxConcurrency,
        MaxConcurrency: maxConcurrency,
        JitterOverride: TimeSpan.Zero);

    [Fact]
    public async Task A_slow_job_does_not_block_top_up_of_a_free_slot()
    {
        var first = TestJobs.Create("test.job");
        var second = TestJobs.Create("test.job");
        var third = TestJobs.Create("test.job");
        var pending = new Queue<Deluno.Jobs.Contracts.JobQueueItem>([first, second, third]);
        var leaseSizes = new ConcurrentQueue<int>();
        var firstRelease = NewSignal();
        var thirdStarted = NewSignal();
        var cancellation = new CancellationTokenSource();
        var running = 0;
        var maximumRunning = 0;

        async Task Execute(Deluno.Jobs.Contracts.JobQueueItem job, CancellationToken _)
        {
            var current = Interlocked.Increment(ref running);
            InterlockedExtensions.Max(ref maximumRunning, current);
            try
            {
                if (job.Id == first.Id)
                {
                    await firstRelease.Task;
                }
                else if (job.Id == third.Id)
                {
                    thirdStarted.TrySetResult();
                    await firstRelease.Task;
                }
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }

        var runnerTask = new LaneRunner().RunAsync(
            TestLane(),
            new SemaphoreSlim(0, 1),
            (availableSlots, _) =>
            {
                var leased = new List<Deluno.Jobs.Contracts.JobQueueItem>();
                while (leased.Count < availableSlots && pending.Count > 0) leased.Add(pending.Dequeue());
                leaseSizes.Enqueue(leased.Count);
                return Task.FromResult<LaneTickResult>(
                    leased.Count == 0
                        ? LaneTickResult.Empty(TimeSpan.FromMilliseconds(20))
                        : new LaneTickResult(leased, TimeSpan.FromMilliseconds(20), leased.Count == availableSlots));
            },
            Execute,
            cancellation.Token);

        await thirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains(2, leaseSizes);
        Assert.Contains(1, leaseSizes);
        Assert.True(maximumRunning <= 2, $"The runner exceeded its two-slot limit: {maximumRunning}.");

        cancellation.Cancel();
        firstRelease.TrySetResult();
        await runnerTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task An_unhandled_job_failure_isolated_to_that_job()
    {
        var jobs = new[] { TestJobs.Create("test.job"), TestJobs.Create("test.job"), TestJobs.Create("test.job") };
        var pending = new Queue<Deluno.Jobs.Contracts.JobQueueItem>(jobs);
        var failures = new ConcurrentQueue<string>();
        var executed = new ConcurrentQueue<string>();
        var cancellation = new CancellationTokenSource();

        var runnerTask = new LaneRunner().RunAsync(
            TestLane(),
            new SemaphoreSlim(0, 1),
            (availableSlots, _) =>
            {
                var leased = new List<Deluno.Jobs.Contracts.JobQueueItem>();
                while (leased.Count < availableSlots && pending.Count > 0) leased.Add(pending.Dequeue());
                return Task.FromResult<LaneTickResult>(
                    leased.Count == 0
                        ? LaneTickResult.Empty(TimeSpan.FromMilliseconds(20))
                        : new LaneTickResult(leased, TimeSpan.FromMilliseconds(20), leased.Count == availableSlots));
            },
            (job, _) =>
            {
                executed.Enqueue(job.Id);
                if (job.Id == jobs[0].Id) throw new InvalidOperationException("broken test job");
                if (job.Id == jobs[2].Id) cancellation.Cancel();
                return Task.CompletedTask;
            },
            cancellation.Token,
            (job, exception) => failures.Enqueue($"{job.Id}:{exception.Message}"));

        await runnerTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(jobs.Select(job => job.Id).OrderBy(id => id), executed.OrderBy(id => id));
        var failure = Assert.Single(failures);
        Assert.StartsWith($"{jobs[0].Id}:", failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_job_that_finishes_before_the_wait_still_wakes_the_next_lease()
    {
        var job = TestJobs.Create("test.job");
        var tickCount = 0;
        var secondTick = NewSignal();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var lane = new JobLane(
            "fast-completion",
            TimeSpan.FromMinutes(5),
            ["test.job"],
            BatchSize: 2,
            MaxConcurrency: 2,
            JitterOverride: TimeSpan.Zero);

        var runnerTask = new LaneRunner().RunAsync(
            lane,
            new SemaphoreSlim(0, 1),
            (_, _) =>
            {
                var current = Interlocked.Increment(ref tickCount);
                if (current == 1)
                {
                    return Task.FromResult(new LaneTickResult([job], lane.Interval));
                }

                secondTick.TrySetResult();
                return Task.FromResult(LaneTickResult.Empty(lane.Interval));
            },
            (_, _) => Task.CompletedTask,
            cancellation.Token);

        await secondTick.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        await runnerTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Shutdown_drains_a_leased_job_before_returning()
    {
        var job = TestJobs.Create("test.job");
        var started = NewSignal();
        var release = NewSignal();
        var cancellation = new CancellationTokenSource();

        var runnerTask = new LaneRunner().RunAsync(
            TestLane(1),
            new SemaphoreSlim(0, 1),
            (_, _) => Task.FromResult<LaneTickResult>(new([job], TimeSpan.FromMilliseconds(20))),
            async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
            },
            cancellation.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Task.Delay(30); // reason: shows a shutdown does not cut off leased work; absence needs real time
        Assert.False(runnerTask.IsCompleted, "A shutdown must drain work that was already leased.");

        release.TrySetResult();
        await runnerTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Cancellation_does_not_report_a_job_failure()
    {
        var job = TestJobs.Create("test.job");
        var started = NewSignal();
        var failures = 0;
        var cancellation = new CancellationTokenSource();

        var runnerTask = new LaneRunner().RunAsync(
            TestLane(1),
            new SemaphoreSlim(0, 1),
            (_, _) => Task.FromResult<LaneTickResult>(new([job], TimeSpan.FromMilliseconds(20))),
            async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token); // reason: a job that runs until the lane is cancelled
            },
            cancellation.Token,
            (_, _) => Interlocked.Increment(ref failures));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await runnerTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task A_tick_that_throws_is_reported_and_retried_instead_of_ending_the_lane()
    {
        var ticks = 0;
        var reported = new ConcurrentQueue<TimeSpan>();
        var recovered = NewSignal();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var runnerTask = new LaneRunner().RunAsync(
            TestLane(1),
            new SemaphoreSlim(0, 1),
            (_, _) =>
            {
                if (Interlocked.Increment(ref ticks) <= 2)
                {
                    throw new InvalidOperationException("database is locked");
                }

                recovered.TrySetResult();
                return Task.FromResult(LaneTickResult.Empty(TimeSpan.FromMilliseconds(20)));
            },
            (_, _) => Task.CompletedTask,
            cancellation.Token,
            onTickFailure: (_, backoff) => reported.Enqueue(backoff),
            maxBackoff: TimeSpan.FromMilliseconds(50));

        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await runnerTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, reported.Count);
        Assert.All(reported, backoff => Assert.True(backoff <= TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task A_wake_signal_does_not_cut_a_failure_backoff_short()
    {
        // The backoff runs on a fake clock, so the rule is asserted exactly:
        // a retry may not come before the backoff has elapsed, and must come
        // once it has. The earlier version measured real elapsed time between
        // retries inside a 900 ms real window, which failed on a loaded Linux
        // runner when a tick or timer was scheduled late.
        var backoff = TimeSpan.FromMilliseconds(300);
        var time = new TimerSignallingTimeProvider();
        var ticks = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var wake = new SemaphoreSlim(0, 1);

        var runnerTask = new LaneRunner().RunAsync(
            TestLane(1),
            wake,
            (_, _) =>
            {
                Interlocked.Increment(ref ticks);
                throw new InvalidOperationException("database is locked");
            },
            (_, _) => Task.CompletedTask,
            cancellation.Token,
            maxBackoff: backoff,
            timeProvider: time);

        for (var retry = 1; retry <= 3; retry++)
        {
            // The runner has ticked and is now sitting in its backoff.
            await time.NextTimer.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(retry, Volatile.Read(ref ticks));

            // Something is queued the whole time the database is struggling.
            try { wake.Release(); } catch (SemaphoreFullException) { }

            time.Advance(backoff - TimeSpan.FromMilliseconds(1));
            // Absence cannot be waited for; give a wrongly-woken runner ample
            // time to show itself. This can only miss a bug, never fail a good run.
            await Task.Delay(100); // reason: absence cannot be waited for; the runner is given real time to wrongly wake
            Assert.Equal(retry, Volatile.Read(ref ticks));

            time.ArmNextTimerSignal();
            time.Advance(TimeSpan.FromMilliseconds(1));
        }

        await time.NextTimer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4, Volatile.Read(ref ticks));

        cancellation.Cancel();
        await runnerTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class TimerSignallingTimeProvider : Microsoft.Extensions.Time.Testing.FakeTimeProvider
    {
        private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task NextTimer => _next.Task;

        public void ArmNextTimerSignal()
            => Interlocked.Exchange(ref _next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _next.TrySetResult();
            return timer;
        }
    }

    [Fact]
    public async Task A_run_of_failed_passes_is_reported_with_its_count_and_cleared_by_a_good_pass()
    {
        var streaks = new ConcurrentQueue<(int Count, string? Error)>();
        var ticks = 0;
        var recovered = NewSignal();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var runnerTask = new LaneRunner().RunAsync(
            TestLane(1),
            new SemaphoreSlim(0, 1),
            (_, _) =>
            {
                if (Interlocked.Increment(ref ticks) <= 3)
                {
                    throw new InvalidOperationException($"locked {ticks}");
                }

                recovered.TrySetResult();
                return Task.FromResult(LaneTickResult.Empty(TimeSpan.FromMilliseconds(20)));
            },
            (_, _) => Task.CompletedTask,
            cancellation.Token,
            maxBackoff: TimeSpan.FromMilliseconds(30),
            onFailureStreak: (count, error) => streaks.Enqueue((count, error?.Message)));

        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await runnerTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([(1, "locked 1"), (2, "locked 2"), (3, "locked 3"), (0, (string?)null)], streaks.ToArray());
    }

    [Fact]
    public void Backoff_doubles_per_failure_and_is_capped()
    {
        var interval = TimeSpan.FromSeconds(5);
        var cap = TimeSpan.FromMinutes(1);
        Assert.Equal(TimeSpan.FromSeconds(10), LaneRunner.BackoffFor(1, interval, cap));
        Assert.Equal(TimeSpan.FromSeconds(20), LaneRunner.BackoffFor(2, interval, cap));
        Assert.Equal(cap, LaneRunner.BackoffFor(8, interval, cap));
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref location);
                if (current >= value || Interlocked.CompareExchange(ref location, value, current) == current) return;
            }
        }
    }
}
