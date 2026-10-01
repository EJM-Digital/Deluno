using Deluno.Infrastructure.SystemReadings;

namespace Deluno.Persistence.Tests.SystemReadings;

/// <summary>
/// #1098: on Linux and in Docker every reading comes from /proc and /sys. These run on any machine
/// because each parser works on the file's text, and the source reads through a fake file system.
/// </summary>
public sealed class LinuxHostReadingTests
{
    private const string ProcStat = """
        cpu  4705 150 1120 16250 520 30 45 10 0 0
        cpu0 1175 37 280 4062 130 7 11 2 0 0
        intr 12345
        """;

    private const string MemInfo = """
        MemTotal:       16384000 kB
        MemFree:         1024000 kB
        MemAvailable:    6144000 kB
        Buffers:          204800 kB
        """;

    private const string DiskStats = """
           8       0 sda 1000 10 80000 500 2000 20 160000 900 0 4000 1400
           8       1 sda1 900 10 70000 450 1900 20 150000 850 0 3500 1300
           7       0 loop0 50 0 400 10 0 0 0 0 0 20 10
         259       0 nvme0n1 5000 0 400000 2000 6000 0 480000 3000 0 8000 5000
         253       0 dm-0 100 0 800 20 100 0 800 20 0 40 40
        """;

    private const string MountInfo = """
        22 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw
        40 22 259:0 / /mnt/media rw,relatime shared:2 - ext4 /dev/nvme0n1 rw
        41 40 0:55 / /mnt/media/nas rw,relatime shared:3 - cifs //nas/media rw
        42 22 8:1 /data /data\040files rw,relatime - ext4 /dev/sda1 rw
        """;

    [Fact]
    public void Processor_times_count_idle_plus_iowait_and_the_first_eight_columns()
    {
        var times = LinuxParsers.ParseCpuTimes(ProcStat);

        Assert.NotNull(times);
        Assert.Equal(16_250UL + 520, times.Value.Idle);
        Assert.Equal(4705UL + 150 + 1120 + 16250 + 520 + 30 + 45 + 10, times.Value.Total);
    }

    [Fact]
    public void Processor_times_are_null_when_the_file_is_missing_or_empty()
    {
        Assert.Null(LinuxParsers.ParseCpuTimes(null));
        Assert.Null(LinuxParsers.ParseCpuTimes(""));
        Assert.Null(LinuxParsers.ParseCpuTimes("intr 1 2 3"));
    }

    [Fact]
    public void Memory_is_total_and_available_in_bytes()
    {
        var memory = LinuxParsers.ParseMemory(MemInfo);

        Assert.Equal((16_384_000L * 1024, 6_144_000L * 1024), memory);
    }

    [Fact]
    public void Memory_is_null_without_an_available_figure()
    {
        Assert.Null(LinuxParsers.ParseMemory("MemTotal: 1000 kB"));
        Assert.Null(LinuxParsers.ParseMemory(null));
    }

    [Fact]
    public void Uptime_is_the_first_number_in_whole_seconds()
    {
        Assert.Equal(350_735, LinuxParsers.ParseUptimeSeconds("350735.47 234388.90\n"));
        Assert.Null(LinuxParsers.ParseUptimeSeconds("nonsense"));
        Assert.Null(LinuxParsers.ParseUptimeSeconds(null));
    }

    [Fact]
    public void The_operating_system_is_its_pretty_name_with_the_kernel_as_the_version()
    {
        var source = new FakeProc
        {
            ["/etc/os-release"] = "NAME=\"Debian GNU/Linux\"\nPRETTY_NAME=\"Debian GNU/Linux 12 (bookworm)\"\nID=debian\n",
            ["/proc/sys/kernel/osrelease"] = "6.1.0-18-amd64\n"
        };

        var os = new LinuxHostReadingSource(source).ReadOperatingSystem();

        Assert.Equal("Debian GNU/Linux 12 (bookworm) (kernel 6.1.0-18-amd64)", os!.Describe());
    }

    [Fact]
    public void Disk_counters_are_keyed_by_device_and_converted_from_sectors()
    {
        var stats = LinuxParsers.ParseDiskStats(DiskStats, uptimeMs: 100_000);

        var sda = stats["8:0"].Counters;
        Assert.Equal(80_000L * 512, sda.BytesRead);
        Assert.Equal(160_000L * 512, sda.BytesWritten);
        Assert.Equal(3_000, sda.Operations);
        Assert.Equal(100_000, sda.QueryTime);
        Assert.Equal(100_000 - 4_000, sda.IdleTime);
        Assert.Equal("nvme0n1", stats["259:0"].Name);
    }

    [Fact]
    public void The_machines_disks_count_each_physical_disk_once()
    {
        var source = new FakeProc
        {
            ["/proc/diskstats"] = DiskStats,
            ["/proc/uptime"] = "100.0 50.0"
        };
        source.Existing.UnionWith(["/sys/block/sda", "/sys/block/loop0", "/sys/block/nvme0n1", "/sys/block/dm-0"]);

        var disks = new LinuxHostReadingSource(source).ReadDisks();

        Assert.NotNull(disks.Machine);
        // sda and nvme0n1 only: not the partition sda1, not loop0, not dm-0.
        Assert.Equal((80_000L + 400_000) * 512, disks.Machine.BytesRead);
        Assert.Equal(1000 + 2000 + 5000 + 6000, disks.Machine.Operations);
        Assert.Contains("8:1", disks.Volumes.Keys);
    }

    [Fact]
    public void A_path_belongs_to_the_mount_that_is_its_longest_matching_prefix()
    {
        Assert.Equal("259:0", LinuxParsers.DeviceOfPath(MountInfo, "/mnt/media/Movies"));
        Assert.Equal("0:55", LinuxParsers.DeviceOfPath(MountInfo, "/mnt/media/nas/Shows"));
        Assert.Equal("8:1", LinuxParsers.DeviceOfPath(MountInfo, "/var/lib/deluno"));
        Assert.Equal("8:1", LinuxParsers.DeviceOfPath(MountInfo, "/data files/x"));
    }

    [Fact]
    public void A_mount_that_only_shares_a_name_prefix_does_not_hold_the_path()
    {
        Assert.Equal("8:1", LinuxParsers.DeviceOfPath(MountInfo, "/mnt/media2/Movies"));
    }

    [Fact]
    public void A_network_mount_has_no_disk_counters_so_the_drive_has_no_activity()
    {
        var source = new FakeProc
        {
            ["/proc/self/mountinfo"] = MountInfo,
            ["/proc/diskstats"] = DiskStats,
            ["/proc/uptime"] = "100.0 50.0"
        };
        var linux = new LinuxHostReadingSource(source);

        var key = linux.VolumeKeyOf("/mnt/media/nas/Shows");

        Assert.Equal("0:55", key);
        Assert.DoesNotContain(key!, linux.ReadDisks().Volumes.Keys);
    }

    [Fact]
    public void The_cpu_temperature_is_the_hottest_processor_sensor_in_degrees()
    {
        var source = new FakeProc
        {
            ["/sys/class/thermal/thermal_zone0/type"] = "x86_pkg_temp\n",
            ["/sys/class/thermal/thermal_zone0/temp"] = "58000\n",
            ["/sys/class/thermal/thermal_zone1/type"] = "acpitz\n",
            ["/sys/class/thermal/thermal_zone1/temp"] = "99000\n",
            ["/sys/class/hwmon/hwmon2/name"] = "k10temp\n",
            ["/sys/class/hwmon/hwmon2/temp1_input"] = "61500\n"
        };
        source.Directories["/sys/class/thermal"] = ["/sys/class/thermal/thermal_zone0", "/sys/class/thermal/thermal_zone1", "/sys/class/thermal/cooling_device0"];
        source.Directories["/sys/class/hwmon"] = ["/sys/class/hwmon/hwmon2"];

        Assert.Equal(61.5, new LinuxHostReadingSource(source).ReadCpuTemperatureC());
    }

    [Fact]
    public void No_sensor_means_no_temperature_not_zero()
    {
        Assert.Null(new LinuxHostReadingSource(new FakeProc()).ReadCpuTemperatureC());
    }

    [Fact]
    public void A_pending_reboot_is_known_only_where_the_system_records_it()
    {
        var pending = new FakeProc();
        pending.Existing.Add("/var/run/reboot-required");
        Assert.True(new LinuxHostReadingSource(pending).ReadRebootPending());

        var debian = new FakeProc();
        debian.Existing.Add("/var/lib/dpkg");
        Assert.False(new LinuxHostReadingSource(debian).ReadRebootPending());

        var container = new FakeProc();
        container.Existing.UnionWith(["/var/lib/dpkg", "/.dockerenv"]);
        Assert.Null(new LinuxHostReadingSource(container).ReadRebootPending());

        Assert.Null(new LinuxHostReadingSource(new FakeProc()).ReadRebootPending());
    }

    [Fact]
    public void A_drive_health_Linux_cannot_read_is_unknown()
    {
        Assert.Equal("unknown", new LinuxHostReadingSource(new FakeProc()).ReadDriveHealth("/mnt/media").Health);
    }

    [Theory]
    [InlineData(1_000_000_000L, 1000L)]
    [InlineData(100_000_000L, 100L)]
    [InlineData(-1L, null)]
    [InlineData(0L, null)]
    public void Link_speed_is_whole_megabits_or_null_when_the_adapter_does_not_know(long bitsPerSecond, long? expected)
    {
        Assert.Equal(expected, DefaultRouteAdapter.SpeedToMbps(bitsPerSecond));
    }

    private sealed class FakeProc : IProcSource
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

        public HashSet<string> Existing { get; } = [];

        public Dictionary<string, string[]> Directories { get; } = new(StringComparer.Ordinal);

        public string this[string path]
        {
            set => _files[path] = value;
        }

        public string? ReadAllText(string path) => _files.GetValueOrDefault(path);

        public bool Exists(string path) => _files.ContainsKey(path) || Existing.Contains(path);

        public IEnumerable<string> ListDirectories(string path) => Directories.GetValueOrDefault(path) ?? [];
    }
}
