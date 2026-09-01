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
        await Task.Delay(30);
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
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            cancellation.Token,
            (_, _) => Interlocked.Increment(ref failures));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await runnerTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, failures);
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
