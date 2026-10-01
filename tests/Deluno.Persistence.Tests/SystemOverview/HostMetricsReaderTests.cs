using Deluno.Infrastructure.SystemReadings;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.SystemReadings;

/// <summary>
/// #1098: rates come from two raw readings of the operating system, and a value the system will not
/// give stays absent instead of becoming a zero.
/// </summary>
public sealed class HostMetricsReaderTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_first_reading_has_no_baseline_and_reports_no_rates()
    {
        var source = new FakeHost();
        var reader = new HostMetricsReader(source, new FakeTimeProvider(Start));

        var first = reader.Read([]);

        Assert.False(first.HasBaseline);
        Assert.Equal(0, first.CpuPercent);
        Assert.Equal(0, first.DiskReadBytesPerSec);
        Assert.Equal(0, first.NetInBytesPerSec);
    }

    [Fact]
    public void Processor_load_is_the_share_of_the_window_that_was_not_idle()
    {
        var clock = new FakeTimeProvider(Start);
        var source = new FakeHost { Cpu = new CpuTimes(Idle: 1_000, Total: 2_000) };
        var reader = new HostMetricsReader(source, clock);
        reader.Read([]);

        clock.Advance(TimeSpan.FromSeconds(2));
        source.Cpu = new CpuTimes(Idle: 1_750, Total: 3_000); // 1000 passed, 750 idle

        var second = reader.Read([]);

        Assert.True(second.HasBaseline);
        Assert.Equal(25, second.CpuPercent);
    }

    [Fact]
    public void Memory_used_is_total_less_available()
    {
        var source = new FakeHost { Memory = (16_000, 6_000) };
        var reader = new HostMetricsReader(source, new FakeTimeProvider(Start));

        var metrics = reader.Read([]);

        Assert.Equal(10_000, metrics.MemoryUsedBytes);
        Assert.Equal(16_000, metrics.MemoryTotalBytes);
        Assert.Equal(62.5, metrics.MemoryPercent);
    }

    [Fact]
    public void Disk_throughput_operations_and_busy_come_from_the_difference_between_readings()
    {
        var clock = new FakeTimeProvider(Start);
        var source = new FakeHost();
        source.SetDisks(machine: new DiskCounters(1_000, 2_000, 10, 0, 0), ("D:", new DiskCounters(1_000, 2_000, 10, 0, 0)));
        var reader = new HostMetricsReader(source, clock);
        reader.Read(["D:\\Downloads"]);

        clock.Advance(TimeSpan.FromSeconds(2));
        // Over 2 s: 6 MB read, 2 MB written, 400 operations, idle for 1.5 of the 2 seconds (100 ns units).
        var later = new DiskCounters(6_001_000, 2_002_000, 410, 15_000_000, 20_000_000);
        source.SetDisks(machine: later, ("D:", later));

        var metrics = reader.Read(["D:\\Downloads"]);

        Assert.Equal(3_000_000, metrics.DiskReadBytesPerSec);
        Assert.Equal(1_000_000, metrics.DiskWriteBytesPerSec);
        Assert.Equal(200, metrics.DiskIops);
        Assert.Equal(25, metrics.DiskBusyPercent);

        var drive = metrics.Drives["D:\\Downloads"];
        Assert.NotNull(drive);
        Assert.Equal(3_000_000, drive.ReadBytesPerSec);
        Assert.Equal(1_000_000, drive.WriteBytesPerSec);
        Assert.Equal(200, drive.Iops);
        Assert.Equal(25, drive.BusyPercent);
    }

    [Fact]
    public void A_drive_the_system_gives_no_counters_for_is_null_not_zero()
    {
        var clock = new FakeTimeProvider(Start);
        var source = new FakeHost();
        source.SetDisks(machine: new DiskCounters(0, 0, 0, 0, 0), ("C:", new DiskCounters(0, 0, 0, 0, 0)));
        var reader = new HostMetricsReader(source, clock);
        reader.Read([@"\\nas\media\Movies"]);
        clock.Advance(TimeSpan.FromSeconds(2));

        var metrics = reader.Read([@"\\nas\media\Movies"]);

        Assert.Null(metrics.Drives[@"\\nas\media\Movies"]);
    }

    [Fact]
    public void A_counter_that_went_backwards_is_not_a_negative_rate()
    {
        var clock = new FakeTimeProvider(Start);
        var source = new FakeHost();
        source.SetDisks(machine: new DiskCounters(9_000, 9_000, 90, 0, 0));
        var reader = new HostMetricsReader(source, clock);
        reader.Read([]);
        clock.Advance(TimeSpan.FromSeconds(2));
        source.SetDisks(machine: new DiskCounters(100, 100, 1, 0, 0));

        var metrics = reader.Read([]);

        Assert.Equal(0, metrics.DiskReadBytesPerSec);
        Assert.Equal(0, metrics.DiskIops);
    }

    [Fact]
    public void Network_speed_is_measured_on_the_adapter_that_carries_the_default_route()
    {
        var clock = new FakeTimeProvider(Start);
        var source = new FakeHost { Adapter = new NetworkAdapterReading("Ethernet", "192.168.1.51", 1000, 1_000, 500) };
        var reader = new HostMetricsReader(source, clock);
        reader.Read([]);
        clock.Advance(TimeSpan.FromSeconds(2));
        source.Adapter = new NetworkAdapterReading("Ethernet", "192.168.1.51", 1000, 4_001_000, 2_000_500);

        var metrics = reader.Read([]);

        Assert.Equal(2_000_000, metrics.NetInBytesPerSec);
        Assert.Equal(1_000_000, metrics.NetOutBytesPerSec);
        Assert.Equal("Ethernet", metrics.Adapter!.Name);
        Assert.Equal(1000, metrics.Adapter.LinkMbps);
    }

    [Fact]
    public void A_different_adapter_is_not_compared_with_the_previous_one()
    {
        var clock = new FakeTimeProvider(Start);
        var source = new FakeHost { Adapter = new NetworkAdapterReading("Ethernet", "192.168.1.51", 1000, 1_000, 500) };
        var reader = new HostMetricsReader(source, clock);
        reader.Read([]);
        clock.Advance(TimeSpan.FromSeconds(2));
        source.Adapter = new NetworkAdapterReading("Wi-Fi", "192.168.1.60", 300, 99_999_999, 99_999_999);

        var metrics = reader.Read([]);

        Assert.Equal(0, metrics.NetInBytesPerSec);
        Assert.Equal("Wi-Fi", metrics.Adapter!.Name);
    }

    [Fact]
    public void No_default_route_means_no_adapter_and_no_speed()
    {
        var clock = new FakeTimeProvider(Start);
        var source = new FakeHost { Adapter = null };
        var reader = new HostMetricsReader(source, clock);
        reader.Read([]);
        clock.Advance(TimeSpan.FromSeconds(2));

        var metrics = reader.Read([]);

        Assert.Null(metrics.Adapter);
        Assert.Equal(0, metrics.NetInBytesPerSec);
    }

    [Fact]
    public void Processor_and_memory_the_system_will_not_give_read_as_zero_without_failing()
    {
        var source = new FakeHost { Cpu = null, Memory = null };
        var reader = new HostMetricsReader(source, new FakeTimeProvider(Start));

        var metrics = reader.Read([]);

        Assert.Equal(0, metrics.MemoryTotalBytes);
        Assert.Equal(0, metrics.MemoryPercent);
        Assert.Equal(0, metrics.CpuPercent);
    }

    [Theory]
    [InlineData(0, 100, 100)]
    [InlineData(100, 100, 0)]
    [InlineData(250, 1000, 75)]
    public void Busy_is_the_share_of_the_window_not_spent_idle(long idle, long query, double expected)
    {
        var busy = HostMetricsReader.BusyPercent(new DiskCounters(0, 0, 0, 0, 0), new DiskCounters(0, 0, 0, idle, query));

        Assert.Equal(expected, busy);
    }

    [Fact]
    public void Process_usage_is_Delunos_own_share_of_the_machine()
    {
        var clock = new FakeTimeProvider(Start);
        var processor = TimeSpan.FromSeconds(10);
        var reader = new ProcessUsageReader(clock, () => (processor, 123_456_789));
        reader.Read();

        clock.Advance(TimeSpan.FromSeconds(10));
        processor += TimeSpan.FromSeconds(Environment.ProcessorCount); // one second of CPU per core per 10 s window

        var usage = reader.Read();

        Assert.Equal(123_456_789, usage.MemoryBytes);
        Assert.Equal(10, usage.CpuPercent);
    }

    /// <summary>A scriptable operating system.</summary>
    internal sealed class FakeHost : IHostReadingSource
    {
        private Dictionary<string, DiskCounters> _volumes = [];
        private DiskCounters? _machine;

        public CpuTimes? Cpu { get; set; } = new CpuTimes(0, 100);
        public (long Total, long Available)? Memory { get; set; } = (8_000, 4_000);
        public long? Uptime { get; set; } = 3_600;
        public OperatingSystemReading? Os { get; set; } = new("Windows 11 Pro", "10.0.26200");
        public bool? RebootPending { get; set; }
        public double? Temperature { get; set; }
        public NetworkAdapterReading? Adapter { get; set; }
        public Dictionary<string, DriveHealthReading> Health { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void SetDisks(DiskCounters? machine, params (string Key, DiskCounters Counters)[] volumes)
        {
            _machine = machine;
            _volumes = volumes.ToDictionary(volume => volume.Key, volume => volume.Counters, StringComparer.OrdinalIgnoreCase);
        }

        public CpuTimes? ReadCpuTimes() => Cpu;

        public (long Total, long Available)? ReadMemory() => Memory;

        public long? ReadUptimeSeconds() => Uptime;

        public OperatingSystemReading? ReadOperatingSystem() => Os;

        public bool? ReadRebootPending() => RebootPending;

        public double? ReadCpuTemperatureC() => Temperature;

        public DiskSnapshot ReadDisks() => new(_volumes, _machine);

        /// <summary>A drive letter is a local volume; a network path has no key.</summary>
        public string? VolumeKeyOf(string path)
            => path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length < 2 || path[1] != ':' ? null : path[..2].ToUpperInvariant();

        public NetworkAdapterReading? ReadDefaultRouteAdapter() => Adapter;

        public DriveHealthReading ReadDriveHealth(string path)
            => Health.TryGetValue(path, out var health) ? health : DriveHealthReading.Unknown;
    }
}
