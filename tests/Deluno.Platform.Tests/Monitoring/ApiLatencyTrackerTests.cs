using Deluno.Api.Monitoring;

namespace Deluno.Platform.Tests.Monitoring;

public sealed class ApiLatencyTrackerTests
{
    [Fact]
    public void Snapshot_reports_count_error_rate_and_p95()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-05-14T01:00:00Z"));
        var tracker = new InMemoryApiLatencyTracker(clock);

        tracker.Record("/api/one", 25, 200);
        tracker.Record("/api/two", 100, 200);
        tracker.Record("/api/three", 260, 503);
        tracker.Record("/api/four", 80, 500);

        var snapshot = tracker.GetSnapshot(TimeSpan.FromMinutes(15));

        Assert.Equal(4, snapshot.RequestCount);
        Assert.Equal(2, snapshot.ErrorCount);
        Assert.Equal(50d, snapshot.ErrorRatePercent);
        Assert.Equal(116.25d, snapshot.AverageMs);
        Assert.Equal(260d, snapshot.P95Ms);
    }

    [Fact]
    public void Snapshot_excludes_samples_outside_window()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-05-14T02:00:00Z"));
        var tracker = new InMemoryApiLatencyTracker(clock);

        tracker.Record("/api/old", 500, 500);
        clock.Advance(TimeSpan.FromMinutes(20));
        tracker.Record("/api/new", 40, 200);

        var snapshot = tracker.GetSnapshot(TimeSpan.FromMinutes(15));

        Assert.Equal(1, snapshot.RequestCount);
        Assert.Equal(0, snapshot.ErrorCount);
        Assert.Equal(40d, snapshot.AverageMs);
        Assert.Equal(40d, snapshot.P95Ms);
    }

    private sealed class MutableTimeProvider(DateTimeOffset startUtc) : TimeProvider
    {
        private DateTimeOffset _now = startUtc;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }
}
