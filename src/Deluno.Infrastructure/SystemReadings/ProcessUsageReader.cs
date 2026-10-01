using System.Diagnostics;

namespace Deluno.Infrastructure.SystemReadings;

/// <summary>Deluno's own share of the machine: processor use over the window since the last reading, and its working set.</summary>
public sealed record ProcessUsage(double CpuPercent, long MemoryBytes);

/// <summary>
/// Reads how hard Deluno itself is working. Kept apart from the machine probe on purpose: both measure
/// a rate between two calls, so sharing one instance with another caller would corrupt the baseline.
/// </summary>
public sealed class ProcessUsageReader(TimeProvider timeProvider, Func<(TimeSpan Processor, long WorkingSet)>? read = null)
{
    private readonly object _gate = new();
    private readonly Func<(TimeSpan Processor, long WorkingSet)> _read = read ?? ReadThisProcess;
    private DateTimeOffset _previousAt;
    private TimeSpan _previousProcessor;

    public ProcessUsage Read()
    {
        var now = timeProvider.GetUtcNow();
        var (processor, workingSet) = _read();
        lock (_gate)
        {
            var seconds = _previousAt == default ? 0 : (now - _previousAt).TotalSeconds;
            var cpu = seconds <= 0
                ? 0
                : Math.Clamp(
                    (processor - _previousProcessor).TotalSeconds / (seconds * Math.Max(1, Environment.ProcessorCount)) * 100d,
                    0,
                    100);
            _previousAt = now;
            _previousProcessor = processor;
            return new ProcessUsage(Math.Round(cpu, 1), workingSet);
        }
    }

    private static (TimeSpan, long) ReadThisProcess()
    {
        using var process = Process.GetCurrentProcess();
        return (process.TotalProcessorTime, process.WorkingSet64);
    }
}
