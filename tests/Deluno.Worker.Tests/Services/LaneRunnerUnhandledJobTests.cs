using Deluno.Worker.Services;
using Deluno.Worker.Tests.Support;

namespace Deluno.Worker.Tests.Services;

/// <summary>#1080: a job that throws, with nobody listening for it, costs that job only; the lane goes on and asks for more.</summary>
public sealed class LaneRunnerUnhandledJobTests
{
    [Fact]
    public async Task A_job_that_throws_with_no_listener_does_not_stop_the_lane()
    {
        var job = TestJobs.Create("test.job");
        var lane = new JobLane("test", TimeSpan.FromHours(1), ["test.job"], BatchSize: 1, MaxConcurrency: 1, JitterOverride: TimeSpan.Zero);
        var ticks = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();

        var run = new LaneRunner().RunAsync(
            lane,
            new SemaphoreSlim(0, 1),
            (_, _) =>
            {
                var tick = Interlocked.Increment(ref ticks);
                if (tick == 2) second.TrySetResult();
                return Task.FromResult(tick == 1
                    ? new LaneTickResult([job], TimeSpan.FromHours(1))
                    : LaneTickResult.Empty(TimeSpan.FromHours(1)));
            },
            (_, _) => throw new InvalidOperationException("the job broke"),
            stop.Token);

        await second.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
    }
}
