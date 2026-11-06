namespace Deluno.Contracts;

/// <summary>
/// A task that is given up on (a connect that outlived its time limit, a probe cut short at shutdown) still finishes,
/// often with a socket error, and a failure nobody reads reaches the finalizer as an unobserved task exception (#1524).
/// <see cref="Observed"/> reads the failure whenever it comes, so abandoning the task is safe.
/// </summary>
public static class AbandonedTasks
{
    /// <summary>Marks the task's failure, if any, as read. Returns the same task for awaiting or waiting as before.</summary>
    public static Task Observed(this Task task)
    {
        _ = task.ContinueWith(
            static finished => _ = finished.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }
}
