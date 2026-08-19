namespace Deluno.Worker.Jobs;

/// <summary>
/// Resolves a job's handler by <see cref="IJobHandler.JobType"/>. An unknown
/// job type throws rather than silently succeeding — routing it to the caller's
/// existing failure handling, which fails the job loudly instead of marking it
/// complete with a message that nothing actually did anything.
/// </summary>
public sealed class JobHandlerRegistry
{
    private readonly IReadOnlyDictionary<string, IJobHandler> _byType;

    public JobHandlerRegistry(IEnumerable<IJobHandler> handlers)
    {
        _byType = handlers.ToDictionary(handler => handler.JobType, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> RegisteredJobTypes => (IReadOnlyCollection<string>)_byType.Keys;

    public IJobHandler Resolve(string jobType)
        => _byType.TryGetValue(jobType, out var handler)
            ? handler
            : throw new InvalidOperationException($"No job handler is registered for job type '{jobType}'.");
}
