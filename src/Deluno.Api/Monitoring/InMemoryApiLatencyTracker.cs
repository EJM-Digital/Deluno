using System.Collections.Concurrent;

namespace Deluno.Api.Monitoring;

public sealed class InMemoryApiLatencyTracker(TimeProvider timeProvider) : IApiLatencyTracker
{
    private readonly ConcurrentQueue<ApiLatencySample> _samples = new();
    private readonly object _trimLock = new();

    public void Record(string path, double durationMs, int statusCode)
    {
        var now = timeProvider.GetUtcNow();
        _samples.Enqueue(new ApiLatencySample(
            Path: string.IsNullOrWhiteSpace(path) ? "unknown" : path,
            DurationMs: Math.Max(0, durationMs),
            StatusCode: statusCode,
            Timestamp: now));
        Trim(now, TimeSpan.FromMinutes(30));
    }

    public ApiLatencySnapshot GetSnapshot(TimeSpan? window = null)
    {
        var now = timeProvider.GetUtcNow();
        var effectiveWindow = window ?? TimeSpan.FromMinutes(15);
        var start = now - effectiveWindow;

        Trim(now, TimeSpan.FromMinutes(30));

        var inWindow = _samples.Where(sample => sample.Timestamp >= start).ToArray();
        if (inWindow.Length == 0)
        {
            return new ApiLatencySnapshot(
                WindowStartUtc: start,
                WindowEndUtc: now,
                RequestCount: 0,
                ErrorCount: 0,
                ErrorRatePercent: 0,
                AverageMs: 0,
                P95Ms: 0);
        }

        var errorCount = inWindow.Count(sample => sample.StatusCode >= 500);
        var ordered = inWindow.Select(sample => sample.DurationMs).OrderBy(value => value).ToArray();
        var p95Index = Math.Clamp((int)Math.Ceiling(ordered.Length * 0.95) - 1, 0, ordered.Length - 1);

        return new ApiLatencySnapshot(
            WindowStartUtc: start,
            WindowEndUtc: now,
            RequestCount: inWindow.Length,
            ErrorCount: errorCount,
            ErrorRatePercent: Math.Round((double)errorCount / inWindow.Length * 100, 2),
            AverageMs: Math.Round(inWindow.Average(sample => sample.DurationMs), 2),
            P95Ms: Math.Round(ordered[p95Index], 2));
    }

    private void Trim(DateTimeOffset now, TimeSpan retention)
    {
        var cutoff = now - retention;
        lock (_trimLock)
        {
            while (_samples.TryPeek(out var sample) && sample.Timestamp < cutoff)
            {
                _samples.TryDequeue(out _);
            }
        }
    }

    private sealed record ApiLatencySample(
        string Path,
        double DurationMs,
        int StatusCode,
        DateTimeOffset Timestamp);
}
