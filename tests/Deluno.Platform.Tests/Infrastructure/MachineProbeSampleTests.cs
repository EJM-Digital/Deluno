using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.SystemReadings;

namespace Deluno.Platform.Tests.Infrastructure;

public sealed class MachineProbeSampleTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-05T09:00:00Z");

    private static MachineProbe Probe() => new(TimeProvider.System);

    private static DiskCounters Disk(long read, long written, long idle, long query) => new(read, written, 0, idle, query);

    [Fact]
    public void The_first_sample_has_nothing_to_measure_against_and_reports_zero()
    {
        var sample = Probe().Sample(Start, TimeSpan.FromSeconds(500), 1000, new ProcessIoCounters(5000, 6000), Disk(1, 2, 3, 4), 8000);

        Assert.Equal(Start, sample.CapturedUtc);
        Assert.Equal(0, sample.CpuPercent);
        Assert.Equal(1000, sample.MemoryBytes);
        Assert.Equal(8000, sample.TotalMemoryBytes);
        Assert.Equal(0, sample.ProcessReadBytesPerSecond);
        Assert.Equal(0, sample.ProcessWriteBytesPerSecond);
        Assert.Null(sample.DiskBusyPercent);
        Assert.Null(sample.DiskReadBytesPerSecond);
        Assert.Null(sample.DiskWriteBytesPerSecond);
    }

    [Fact]
    public void The_next_sample_reports_cpu_and_byte_rates_over_the_time_between()
    {
        var probe = Probe();
        probe.Sample(Start, TimeSpan.Zero, 1, new ProcessIoCounters(1000, 2000), Disk(0, 0, 0, 0), null);

        var sample = probe.Sample(Start.AddSeconds(10), TimeSpan.FromSeconds(1), 1, new ProcessIoCounters(1500, 2100), Disk(1000, 3000, 500_000, 1_000_000), null);

        Assert.Equal(Math.Round(1d / (10 * Environment.ProcessorCount) * 100, 1), sample.CpuPercent);
        Assert.Equal(50, sample.ProcessReadBytesPerSecond);
        Assert.Equal(10, sample.ProcessWriteBytesPerSecond);
        Assert.Equal(50, sample.DiskBusyPercent);
        Assert.Equal(100, sample.DiskReadBytesPerSecond);
        Assert.Equal(300, sample.DiskWriteBytesPerSecond);
        Assert.Null(sample.TotalMemoryBytes);
    }

    [Fact]
    public void Cpu_use_is_held_between_nothing_and_everything()
    {
        var probe = Probe();
        probe.Sample(Start, TimeSpan.Zero, 1, null, null, null);

        Assert.Equal(100, probe.Sample(Start.AddSeconds(1), TimeSpan.FromHours(10), 1, null, null, null).CpuPercent);
        Assert.Equal(0, probe.Sample(Start.AddSeconds(2), TimeSpan.Zero, 1, null, null, null).CpuPercent);
    }

    [Fact]
    public void Busy_time_is_held_between_nothing_and_everything_and_unknown_when_the_window_is_empty()
    {
        var probe = Probe();
        probe.Sample(Start, TimeSpan.Zero, 1, null, Disk(0, 0, 100, 100), null);

        Assert.Equal(0, probe.Sample(Start.AddSeconds(1), TimeSpan.Zero, 1, null, Disk(0, 0, 1_100, 1_100), null).DiskBusyPercent);
        Assert.Equal(100, probe.Sample(Start.AddSeconds(2), TimeSpan.Zero, 1, null, Disk(0, 0, 1_100, 2_100), null).DiskBusyPercent);
        Assert.Equal(100, probe.Sample(Start.AddSeconds(3), TimeSpan.Zero, 1, null, Disk(0, 0, 0, 3_100), null).DiskBusyPercent);

        var empty = probe.Sample(Start.AddSeconds(4), TimeSpan.Zero, 1, null, Disk(10, 20, 0, 3_100), null);

        Assert.Null(empty.DiskBusyPercent);
        Assert.Equal(10, empty.DiskReadBytesPerSecond);
        Assert.Equal(20, empty.DiskWriteBytesPerSecond);
    }

    [Fact]
    public void Counters_that_went_backwards_report_no_rate_and_zero_for_the_program()
    {
        var probe = Probe();
        probe.Sample(Start, TimeSpan.Zero, 1, new ProcessIoCounters(1000, 1000), Disk(1000, 1000, 0, 0), null);

        var sample = probe.Sample(Start.AddSeconds(1), TimeSpan.Zero, 1, new ProcessIoCounters(10, 10), Disk(10, 10, 0, 10), null);

        Assert.Equal(0, sample.ProcessReadBytesPerSecond);
        Assert.Equal(0, sample.ProcessWriteBytesPerSecond);
        Assert.Null(sample.DiskReadBytesPerSecond);
        Assert.Null(sample.DiskWriteBytesPerSecond);
    }

    [Fact]
    public void A_sample_taken_at_the_same_moment_measures_nothing()
    {
        var probe = Probe();
        probe.Sample(Start, TimeSpan.Zero, 1, new ProcessIoCounters(0, 0), Disk(0, 0, 0, 0), null);

        var sample = probe.Sample(Start, TimeSpan.FromSeconds(5), 1, new ProcessIoCounters(500, 500), Disk(500, 500, 0, 500), null);

        Assert.Equal(0, sample.CpuPercent);
        Assert.Equal(0, sample.ProcessReadBytesPerSecond);
        Assert.Null(sample.DiskBusyPercent);
        Assert.Null(sample.DiskReadBytesPerSecond);
    }

    [Fact]
    public void A_missing_reading_keeps_the_last_one_to_measure_the_next_against()
    {
        var probe = Probe();
        probe.Sample(Start, TimeSpan.Zero, 1, new ProcessIoCounters(1000, 1000), Disk(1000, 1000, 0, 0), null);
        var skipped = probe.Sample(Start.AddSeconds(5), TimeSpan.Zero, 1, null, null, null);

        Assert.Equal(0, skipped.ProcessReadBytesPerSecond);
        Assert.Null(skipped.DiskReadBytesPerSecond);

        var next = probe.Sample(Start.AddSeconds(10), TimeSpan.Zero, 1, new ProcessIoCounters(2000, 1000), Disk(3000, 1000, 0, 10), null);

        Assert.Equal(200, next.ProcessReadBytesPerSecond);
        Assert.Equal(400, next.DiskReadBytesPerSecond);
    }

    [Fact]
    public void A_disk_that_appears_late_is_not_measured_against_nothing()
    {
        var probe = Probe();
        probe.Sample(Start, TimeSpan.Zero, 1, null, null, null);

        var sample = probe.Sample(Start.AddSeconds(5), TimeSpan.Zero, 1, null, Disk(100, 100, 0, 100), null);

        Assert.Null(sample.DiskBusyPercent);
    }

    [Fact]
    public void The_machines_memory_is_what_windows_says_otherwise_what_the_runtime_can_see()
    {
        Assert.Equal(16, MachineProbe.TotalMemoryOf((16, 4), 99));
        Assert.Equal(99, MachineProbe.TotalMemoryOf(null, 99));
        Assert.Null(MachineProbe.TotalMemoryOf(null, 0));
        Assert.Null(MachineProbe.TotalMemoryOf(null, -1));
    }

    [Fact]
    public void Reading_this_machine_gives_a_sample_and_a_volume_that_cannot_be_read_is_left_out()
    {
        var probe = Probe();

        var first = probe.Read(null);
        var second = probe.Read(Path.GetTempPath());
        var odd = probe.Read("bad\0path");

        Assert.True(first.MemoryBytes > 0);
        Assert.True(second.MemoryBytes > 0);
        Assert.Null(odd.DiskBusyPercent);
        Assert.Null(probe.Read("   ").DiskBusyPercent);
    }
}
