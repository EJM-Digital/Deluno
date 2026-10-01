namespace Deluno.Infrastructure.SystemReadings;

/// <summary>The last ten minutes of four readings, one every ten seconds, oldest first.</summary>
public sealed record OverviewHistorySnapshot(
    IReadOnlyList<double> Cpu,
    IReadOnlyList<double> Memory,
    IReadOnlyList<double> Disk,
    IReadOnlyList<double> NetIn);

/// <summary>
/// An in-memory ring of the last ten minutes of processor, memory, disk-busy and network-in readings.
/// It is offered a reading every couple of seconds and keeps one every ten, so the charts on the screen
/// always have the same, steady resolution however often the sampler runs.
/// </summary>
public sealed class OverviewHistory(TimeProvider timeProvider)
{
    public static readonly TimeSpan Resolution = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly Queue<Point> _points = new();

    private readonly record struct Point(DateTimeOffset At, double Cpu, double Memory, double Disk, double NetIn);

    /// <summary>Offers a reading; it is kept only when ten seconds have passed since the last one kept.</summary>
    /// <returns>True when it was kept.</returns>
    public bool Offer(double cpu, double memory, double disk, double netIn)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_points.Count > 0 && now - _points.Last().At < Resolution - TimeSpan.FromMilliseconds(500))
            {
                return false;
            }

            _points.Enqueue(new Point(now, cpu, memory, disk, netIn));
            Trim(now);
            return true;
        }
    }

    public OverviewHistorySnapshot Snapshot()
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            Trim(now);
            var points = _points.ToArray();
            return new OverviewHistorySnapshot(
                points.Select(point => point.Cpu).ToArray(),
                points.Select(point => point.Memory).ToArray(),
                points.Select(point => point.Disk).ToArray(),
                points.Select(point => point.NetIn).ToArray());
        }
    }

    private void Trim(DateTimeOffset now)
    {
        while (_points.Count > 0 && now - _points.Peek().At > Window)
        {
            _points.Dequeue();
        }
    }
}
