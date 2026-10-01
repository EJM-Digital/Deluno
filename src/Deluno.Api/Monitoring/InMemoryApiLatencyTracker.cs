using System.Collections.Concurrent;

namespace Deluno.Api.Monitoring;

public sealed class InMemoryApiLatencyTracker(TimeProvider timeProvider) : IApiLatencyTracker
{
    private readonly ConcurrentQueue<ApiLatencySample> _samples = new();
    private readonly object _trimLock = new();
    private readonly object _dayLock = new();
    private DateTime _dayLocal = DateTime.MinValue;
    private DateTimeOffset _daySinceUtc;
    private long _dayRequests;
    private long _dayErrors;


    public void Record(string path, double durationMs, int statusCode)
    {
        var now = timeProvider.GetUtcNow();
        _samples.Enqueue(new ApiLatencySample(
            Path: string.IsNullOrWhiteSpace(path) ? "unknown" : path,
            DurationMs: Math.Max(0, durationMs),
            StatusCode: statusCode,
            Timestamp: now));
        CountForToday(now, statusCode);
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


    public ApiAnswerTimes GetAnswerTimes(TimeSpan window)
    {
        var now = timeProvider.GetUtcNow();
        Trim(now, TimeSpan.FromMinutes(30));
        var start = now - window;
        var ordered = _samples
            .Where(sample => sample.Timestamp >= start)
            .Select(sample => sample.DurationMs)
            .OrderBy(value => value)
            .ToArray();
        if (ordered.Length == 0)
        {
            return new ApiAnswerTimes(0, 0, 0);
        }

        var middle = ordered.Length / 2;
        var typical = ordered.Length % 2 == 1 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2d;
        var slowIndex = Math.Clamp((int)Math.Ceiling(ordered.Length * 0.95) - 1, 0, ordered.Length - 1);
        return new ApiAnswerTimes(ordered.Length, Math.Round(typical, 1), Math.Round(ordered[slowIndex], 1));
    }

    public ApiDayTotals GetDayTotals()
    {
        var now = timeProvider.GetUtcNow();
        lock (_dayLock)
        {
            RollDay(now);
            return new ApiDayTotals(_dayRequests, _dayErrors, _daySinceUtc);
        }
    }

    private void CountForToday(DateTimeOffset now, int statusCode)
    {
        lock (_dayLock)
        {
            RollDay(now);
            _dayRequests++;
            if (statusCode >= 500)
            {
                _dayErrors++;
            }
        }
    }

    /// <summary>A new local day starts the counts again; the first one starts when Deluno did.</summary>
    private void RollDay(DateTimeOffset now)
    {
        var today = timeProvider.GetLocalNow().Date;
        if (_dayLocal == today)
        {
            return;
        }

        _dayLocal = today;
        _daySinceUtc = _daySinceUtc == default ? now : new DateTimeOffset(today, timeProvider.GetLocalNow().Offset).ToUniversalTime();
        _dayRequests = 0;
        _dayErrors = 0;
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
