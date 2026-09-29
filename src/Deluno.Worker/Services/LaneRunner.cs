using Deluno.Jobs.Contracts;

namespace Deluno.Worker.Services;

/// <summary>
/// Runs one worker lane without making a batch a barrier.
///
/// A lane leases only the slots it has available. Once a job finishes, the
/// runner wakes and asks for one more job; a slow job therefore occupies one
/// slot rather than holding the rest of the lane idle. The wake semaphore and
/// the completion task are raced together so a lane reacts to either newly
/// queued work or a freed slot without polling.
/// </summary>
public sealed class LaneRunner
{
    public async Task RunAsync(
        JobLane lane,
        SemaphoreSlim wake,
        Func<int, CancellationToken, Task<LaneTickResult>> tick,
        Func<JobQueueItem, CancellationToken, Task> execute,
        CancellationToken cancellationToken,
        Action<JobQueueItem, Exception>? onUnhandledJobFailure = null,
        Action<Exception, TimeSpan>? onTickFailure = null,
        TimeSpan? maxBackoff = null)
    {
        var backoffCap = maxBackoff ?? TimeSpan.FromMinutes(1);
        var consecutiveTickFailures = 0;
        var inFlight = new List<Task>();
        var drainImmediately = true;
        var sleepFor = lane.Interval;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // A short job can finish between the previous iteration adding
                // it and this one beginning. Remember that completion before
                // pruning the task; otherwise the lane erases its own wake and
                // sleeps for the five-minute backstop instead of recalculating
                // a failed job's retry deadline.
                var jobCompletedBeforeWait = inFlight.Any(task => task.IsCompleted);
                RemoveCompleted(inFlight);

                if (!drainImmediately && !jobCompletedBeforeWait)
                {
                    await WaitForWakeOrCompletionAsync(wake, sleepFor, inFlight, cancellationToken);
                }

                drainImmediately = false;
                sleepFor = lane.Interval;
                RemoveCompleted(inFlight);

                var freeSlots = lane.MaxConcurrency - inFlight.Count;
                if (freeSlots <= 0)
                {
                    await Task.WhenAny(inFlight);
                    continue;
                }

                var availableSlots = Math.Min(lane.BatchSize, freeSlots);
                LaneTickResult result;
                try
                {
                    result = await tick(availableSlots, cancellationToken);
                    consecutiveTickFailures = 0;
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // One failed tick (a busy or slow database on a starved
                    // machine, a bad row) must never end the lane: an exception
                    // out of here stops the whole Deluno host (#1080). Log it,
                    // back off, and try again.
                    consecutiveTickFailures++;
                    var backoff = BackoffFor(consecutiveTickFailures, lane.Interval, backoffCap);
                    onTickFailure?.Invoke(exception, backoff);
                    sleepFor = backoff;
                    continue;
                }

                sleepFor = result.SleepFor;

                if (result.Jobs.Count == 0)
                {
                    continue;
                }

                if (result.Jobs.Count > availableSlots)
                {
                    throw new InvalidOperationException(
                        $"Lane '{lane.Name}' returned {result.Jobs.Count} jobs for {availableSlots} available slot(s).");
                }

                drainImmediately = result.DrainImmediately;
                foreach (var job in result.Jobs)
                {
                    inFlight.Add(RunSafelyAsync(job, execute, cancellationToken, onUnhandledJobFailure));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stop asking for work, then drain the jobs already leased below.
        }

        if (inFlight.Count > 0)
        {
            await Task.WhenAll(inFlight);
        }
    }

    /// <summary>The lane's own interval, doubled for each failure in a row, capped.</summary>
    internal static TimeSpan BackoffFor(int consecutiveFailures, TimeSpan interval, TimeSpan cap)
    {
        var baseTicks = Math.Max(interval.Ticks, TimeSpan.FromSeconds(1).Ticks);
        var factor = Math.Pow(2, Math.Min(Math.Max(consecutiveFailures, 1), 10));
        var ticks = Math.Min((double)cap.Ticks, baseTicks * factor);
        return TimeSpan.FromTicks((long)ticks);
    }

    private static void RemoveCompleted(List<Task> inFlight)
    {
        inFlight.RemoveAll(task => task.IsCompleted);
    }

    private static async Task WaitForWakeOrCompletionAsync(
        SemaphoreSlim wake,
        TimeSpan timeout,
        IReadOnlyCollection<Task> inFlight,
        CancellationToken cancellationToken)
    {
        if (inFlight.Count == 0)
        {
            await wake.WaitAsync(timeout, cancellationToken);
            return;
        }

        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var wakeTask = wake.WaitAsync(timeout, waitCancellation.Token);
        var completionTask = Task.WhenAny(inFlight);
        var winner = await Task.WhenAny(wakeTask, completionTask);

        if (winner == completionTask)
        {
            waitCancellation.Cancel();
            try
            {
                await wakeTask;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A job completed first. Cancelling the unused semaphore wait
                // is cleanup, not a lane cancellation.
            }
        }
        else
        {
            await wakeTask;
        }
    }

    private static async Task RunSafelyAsync(
        JobQueueItem job,
        Func<JobQueueItem, CancellationToken, Task> execute,
        CancellationToken cancellationToken,
        Action<JobQueueItem, Exception>? onUnhandledJobFailure)
    {
        try
        {
            await execute(job, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown is not a job failure. The owner leaves its lease for
            // expiry so a later worker can recover it.
        }
        catch (Exception exception)
        {
            onUnhandledJobFailure?.Invoke(job, exception);
        }
    }
}

public sealed record LaneTickResult(
    IReadOnlyList<JobQueueItem> Jobs,
    TimeSpan SleepFor,
    bool DrainImmediately = false)
{
    public static LaneTickResult Empty(TimeSpan sleepFor) => new([], sleepFor);
}
