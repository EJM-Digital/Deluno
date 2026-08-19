using System.Collections.Concurrent;

namespace Deluno.Jobs.Data;

public sealed class JobLaneSignal : IJobLaneSignal
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _byJobType =
        new(StringComparer.OrdinalIgnoreCase);

    public SemaphoreSlim Register(string laneName, IReadOnlyList<string> jobTypes)
    {
        var gate = new SemaphoreSlim(0, 1);
        foreach (var jobType in jobTypes)
        {
            _byJobType[jobType] = gate;
        }

        return gate;
    }

    public void Notify(string jobType)
    {
        if (!_byJobType.TryGetValue(jobType, out var gate))
        {
            return; // No lane claims this type. The backstop tick still covers it.
        }

        try
        {
            gate.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled. One wake-up is enough.
        }
    }
}
