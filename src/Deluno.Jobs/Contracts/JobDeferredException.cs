namespace Deluno.Jobs.Contracts;

/// <summary>
/// Thrown by a job handler that cannot do its work yet and should run again
/// after <see cref="Delay"/> (#598). The worker puts the job back in the queue
/// without counting it as a failed attempt, so nothing is dead-lettered and
/// nothing is reported as broken.
/// </summary>
public sealed class JobDeferredException(TimeSpan delay, string reason) : Exception(reason)
{
    public TimeSpan Delay { get; } = delay;
}
