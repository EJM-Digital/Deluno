namespace Deluno.Jobs.Data;

/// <summary>
/// Lets whoever enqueues work wake the lane that will run it, so a lane runs
/// because work exists rather than because a timer fired.
/// </summary>
public interface IJobLaneSignal
{
    /// <summary>Called once per lane by the worker at start-up.</summary>
    SemaphoreSlim Register(string laneName, IReadOnlyList<string> jobTypes);

    /// <summary>Called after a job of this type is committed and is ready to run.</summary>
    void Notify(string jobType);
}
