using Deluno.Infrastructure.SystemReadings;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.SystemReadings;

/// <summary>#1098: the charts are the last ten minutes at one reading every ten seconds, however often the sampler offers one.</summary>
public sealed class OverviewHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Readings_offered_every_two_seconds_are_kept_every_ten()
    {
        var clock = new FakeTimeProvider(Start);
        var history = new OverviewHistory(clock);

        var kept = 0;
        for (var second = 0; second <= 30; second += 2)
        {
            if (history.Offer(second, 0, 0, 0))
            {
                kept++;
            }

            clock.Advance(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(4, kept);
        Assert.Equal([0d, 10d, 20d, 30d], history.Snapshot().Cpu);
    }

    [Fact]
    public void Only_the_last_ten_minutes_are_kept_oldest_first()
    {
        var clock = new FakeTimeProvider(Start);
        var history = new OverviewHistory(clock);

        for (var i = 0; i < 100; i++)
        {
            history.Offer(i, i + 1000, i + 2000, i + 3000);
            clock.Advance(OverviewHistory.Resolution);
        }

        var snapshot = history.Snapshot();
        Assert.InRange(snapshot.Cpu.Count, 60, 61);
        Assert.Equal(snapshot.Cpu.Count, snapshot.Memory.Count);
        Assert.Equal(snapshot.Cpu.Count, snapshot.Disk.Count);
        Assert.Equal(snapshot.Cpu.Count, snapshot.NetIn.Count);
        Assert.Equal(99d, snapshot.Cpu[^1]);
        Assert.Equal(1099d, snapshot.Memory[^1]);
        Assert.Equal(2099d, snapshot.Disk[^1]);
        Assert.Equal(3099d, snapshot.NetIn[^1]);
        Assert.True(snapshot.Cpu[0] < snapshot.Cpu[1]);
    }

    [Fact]
    public void An_old_gap_does_not_leave_stale_points_behind()
    {
        var clock = new FakeTimeProvider(Start);
        var history = new OverviewHistory(clock);
        history.Offer(50, 50, 50, 50);

        clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Empty(history.Snapshot().Cpu);
    }

    [Fact]
    public void Nothing_offered_is_an_empty_chart()
    {
        var history = new OverviewHistory(new FakeTimeProvider(Start));

        var snapshot = history.Snapshot();

        Assert.Empty(snapshot.Cpu);
        Assert.Empty(snapshot.NetIn);
    }
}
