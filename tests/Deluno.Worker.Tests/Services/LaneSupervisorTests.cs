using Deluno.Contracts;
using Deluno.Worker.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Worker.Tests.Services;

/// <summary>#1080: a dead or persistently failing job lane must reach Health.</summary>
public sealed class LaneSupervisorTests
{
    private sealed class RecordingReporter : IBackgroundProblemReporter
    {
        private readonly Dictionary<string, (string Title, string Reason)> _active = [];

        public IReadOnlyDictionary<string, (string Title, string Reason)> Active
        {
            get { lock (_active) { return new Dictionary<string, (string, string)>(_active); } }
        }

        public void Report(string key, string title, string message, string reason)
        {
            lock (_active) { _active[key] = (title, reason); }
        }

        public void Clear(string key)
        {
            lock (_active) { _active.Remove(key); }
        }
    }

    [Fact]
    public async Task A_lane_that_ends_with_an_error_is_reported_and_started_again()
    {
        var reporter = new RecordingReporter();
        var supervisor = new LaneSupervisor(NullLogger.Instance, reporter, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20));
        var runs = 0;
        var secondRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var task = supervisor.SuperviseAsync("import", async token =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                throw new InvalidOperationException("returned 3 jobs for 2 slots");
            }

            secondRun.TrySetResult();
            await Task.Delay(Timeout.Infinite, token); // reason: a lane that runs until the supervisor cancels it
        }, stop.Token);

        await secondRun.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var problem = Assert.Single(reporter.Active);
        Assert.Equal("lane:import", problem.Key);
        Assert.Contains("import", problem.Value.Title);
        Assert.Equal("returned 3 jobs for 2 slots", problem.Value.Reason);

        stop.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_lane_that_returns_quietly_while_running_is_also_restarted()
    {
        var reporter = new RecordingReporter();
        var supervisor = new LaneSupervisor(NullLogger.Instance, reporter, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20));
        var runs = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var task = supervisor.SuperviseAsync("search", async token =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                return;
            }

            second.TrySetResult();
            await Task.Delay(Timeout.Infinite, token); // reason: a lane that runs until the supervisor cancels it
        }, stop.Token);

        await second.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("lane:search", reporter.Active.Keys);

        stop.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Shutdown_is_not_reported_as_a_lane_failure()
    {
        var reporter = new RecordingReporter();
        var supervisor = new LaneSupervisor(NullLogger.Instance, reporter);
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = supervisor.SuperviseAsync("import", async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token); // reason: a lane that runs until the supervisor cancels it
        }, stop.Token);

        await started.Task;
        stop.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(reporter.Active);
    }

    [Fact]
    public void A_lane_failing_three_times_in_a_row_shows_on_Health_with_the_last_error_and_clears_when_a_pass_works()
    {
        var reporter = new RecordingReporter();
        var supervisor = new LaneSupervisor(NullLogger.Instance, reporter);

        supervisor.OnFailureStreak("import", 1, new InvalidOperationException("locked 1"));
        supervisor.OnFailureStreak("import", 2, new InvalidOperationException("locked 2"));
        Assert.Empty(reporter.Active);

        supervisor.OnFailureStreak("import", 3, new InvalidOperationException("locked 3"));
        var problem = Assert.Single(reporter.Active);
        Assert.Equal("lane:import", problem.Key);
        Assert.Contains("import", problem.Value.Title);
        Assert.Equal("locked 3", problem.Value.Reason);

        supervisor.OnFailureStreak("import", 0, null);
        Assert.Empty(reporter.Active);
    }
}
