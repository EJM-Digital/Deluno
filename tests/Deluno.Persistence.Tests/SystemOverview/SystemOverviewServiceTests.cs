using System.Text.Json;
using Deluno.Api.Monitoring;
using Deluno.Contracts;
using Deluno.Infrastructure.SystemReadings;
using Deluno.Worker.SystemReadings;
using Microsoft.Extensions.Time.Testing;

namespace Deluno.Persistence.Tests.SystemReadings;

/// <summary>
/// #1098: the overview is assembled from real readings; whatever cannot be read is null on the wire, and
/// the JSON field names are the contract the screen is built against.
/// </summary>
public sealed class SystemOverviewServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public FakeTimeProvider Clock { get; } = new(Start);
        public HostMetricsReaderTests.FakeHost Host { get; } = new();
        public FakeFacts Facts { get; } = new();
        public InMemoryApiLatencyTracker Latency { get; }
        public FakeProbe Probe { get; } = new();
        public SystemOverviewService Service { get; }

        public Rig()
        {
            Clock.SetLocalTimeZone(TimeZoneInfo.Utc);
            Latency = new InMemoryApiLatencyTracker(Clock);
            var metrics = new HostMetricsReader(Host, Clock);
            Host.Cpu = new CpuTimes(0, 1_000);
            Host.SetDisks(new DiskCounters(0, 0, 0, 0, 0), ("D:", new DiskCounters(0, 0, 0, 0, 0)));
            Host.Adapter = new NetworkAdapterReading("Ethernet", "192.168.1.51", 1000, 0, 0);
            metrics.Read(["D:\\Downloads"]); // the baseline the next reading is measured against
            Clock.Advance(TimeSpan.FromSeconds(2));

            Service = new SystemOverviewService(
                Host,
                metrics,
                new ProcessUsageReader(Clock, () => (TimeSpan.Zero, 200_000_000)),
                new OverviewHistory(Clock),
                new NetworkChecker(Probe, Clock, TimeSpan.FromSeconds(1)),
                Facts,
                Latency,
                Clock);
        }

        public async Task<SystemOverview> SampleAsync(bool advance = true)
        {
            if (advance)
            {
                Clock.Advance(TimeSpan.FromSeconds(2));
            }

            return await Service.SampleAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_overview_carries_real_readings_from_every_source()
    {
        var rig = new Rig();
        rig.Host.Cpu = new CpuTimes(750, 2_000);
        rig.Host.Memory = (16_000_000_000, 4_000_000_000);
        rig.Host.Uptime = 90_061;
        rig.Host.Os = new OperatingSystemReading("Windows 11 Pro", "10.0.26200");
        rig.Host.RebootPending = true;
        rig.Host.Temperature = 61.5;
        rig.Host.SetDisks(
            new DiskCounters(6_000_000, 2_000_000, 400, 15_000_000, 20_000_000),
            ("D:", new DiskCounters(6_000_000, 2_000_000, 400, 15_000_000, 20_000_000)));
        rig.Host.Adapter = new NetworkAdapterReading("Ethernet", "192.168.1.51", 1000, 4_000_000, 2_000_000);
        rig.Host.Health["D:\\Downloads"] = new DriveHealthReading("warning", "1 reallocated sector");
        rig.Facts.Drives = [new DriveFacts("D:", "Downloads", "D:\\Downloads", 2_000_000_000_000, 800_000_000_000, 600_000_000_000, 40)];
        rig.Probe.Lookup = 14;
        rig.Probe.Reach = 2.5;
        rig.Probe.Http = new HttpTiming(120, Start.AddSeconds(2));
        rig.Latency.Record("/api/a", 10, 200);
        rig.Latency.Record("/api/b", 30, 500);

        // Warm the slow readings (they refresh in the background), then take the reading that counts.
        await rig.SampleAsync();
        await WaitUntilAsync(async () => (await rig.SampleAsync()).Machine.CpuTemperatureC is not null
            && (await rig.Service.GetAsync(CancellationToken.None)).Network.DnsLookupMs is not null);
        var overview = await rig.Service.GetAsync(CancellationToken.None);

        Assert.Equal("1.0.0-rc.99", overview.Instance.Version);
        Assert.Equal("Windows 11 Pro (10.0.26200)", overview.Machine.Os);
        Assert.Equal(90_061, overview.Machine.UptimeSeconds);
        Assert.True(overview.Machine.RebootPending);
        Assert.Equal(61.5, overview.Machine.CpuTemperatureC);
        Assert.Equal(16_000_000_000, overview.Machine.MemoryTotalBytes);
        Assert.Equal(12_000_000_000, overview.Machine.MemoryUsedBytes);
        Assert.Equal("Ethernet", overview.Network.Adapter);
        Assert.Equal(1000, overview.Network.LinkMbps);
        Assert.Equal("192.168.1.51", overview.Network.Ip);
        Assert.Equal(14, overview.Network.DnsLookupMs);
        Assert.Equal(2.5, overview.Network.NasMs);
        Assert.Equal("nas", overview.Network.NasName);
        Assert.Equal(120, overview.Network.MetadataMs);
        Assert.True(overview.Network.ClockInSync);
        Assert.True(overview.Network.Internet);

        var drive = Assert.Single(overview.Drives);
        Assert.Equal("Downloads", drive.Name);
        Assert.Equal(800_000_000_000, drive.FreeBytes);
        Assert.Equal(600_000_000_000, drive.DelunoBytes);
        Assert.Equal(40, drive.FullInDays);
        Assert.Equal("warning", drive.Health);
        Assert.Equal("1 reallocated sector", drive.HealthNote);
    }

    [Fact]
    public async Task Disk_and_network_speeds_are_rates_over_the_window_between_readings()
    {
        var rig = new Rig();
        rig.Host.Cpu = new CpuTimes(750, 2_000);
        rig.Host.SetDisks(
            new DiskCounters(6_000_000, 2_000_000, 400, 15_000_000, 20_000_000),
            ("D:", new DiskCounters(6_000_000, 2_000_000, 400, 15_000_000, 20_000_000)));
        rig.Host.Adapter = new NetworkAdapterReading("Ethernet", "192.168.1.51", 1000, 4_000_000, 2_000_000);
        rig.Facts.Drives = [new DriveFacts("D:", "Downloads", "D:\\Downloads", 100, 50, 10, null)];

        await rig.SampleAsync(advance: false);
        var overview = await rig.Service.GetAsync(CancellationToken.None);

        Assert.Equal(3_000_000, overview.Machine.DiskReadBytesPerSec);
        Assert.Equal(1_000_000, overview.Machine.DiskWriteBytesPerSec);
        Assert.Equal(200, overview.Machine.DiskIops);
        Assert.Equal(2_000_000, overview.Network.InBytesPerSec);
        Assert.Equal(1_000_000, overview.Network.OutBytesPerSec);
        var drive = Assert.Single(overview.Drives);
        Assert.Equal(3_000_000, drive.ReadBytesPerSec);
        Assert.Equal(1_000_000, drive.WriteBytesPerSec);
        Assert.Equal(200, drive.Iops);
        Assert.Equal(25, drive.BusyPercent);
    }

    [Fact]
    public async Task Whatever_cannot_be_read_is_null_and_never_made_up()
    {
        var rig = new Rig();
        rig.Host.Os = null;
        rig.Host.RebootPending = null;
        rig.Host.Temperature = null;
        rig.Host.Adapter = null;
        rig.Probe.Lookup = null;
        rig.Probe.Http = null;
        rig.Facts.Instance = rig.Facts.Instance with { NasHost = null, MetadataUrl = null, LastBackupUtc = null, LastBackupBytes = null, OldestWaitingSeconds = null };
        rig.Facts.Drives = [new DriveFacts("net", "Library", @"\\nas\media\Movies", 8_000, 2_000, 1_000, null)];

        await rig.SampleAsync();
        await Task.Delay(200); // reason: lets the first sample's background probes finish before the second reads them; they expose no signal
        var overview = await rig.SampleAsync();

        Assert.Null(overview.Machine.RebootPending);
        Assert.Null(overview.Machine.CpuTemperatureC);
        Assert.False(string.IsNullOrWhiteSpace(overview.Machine.Os));
        Assert.Null(overview.Network.Adapter);
        Assert.Null(overview.Network.LinkMbps);
        Assert.Null(overview.Network.Ip);
        Assert.Null(overview.Network.DnsLookupMs);
        Assert.Null(overview.Network.NasMs);
        Assert.Null(overview.Network.NasName);
        Assert.Null(overview.Network.MetadataMs);
        Assert.Null(overview.Network.ClockInSync);
        Assert.Null(overview.Instance.LastBackupUtc);
        Assert.Null(overview.Instance.LastBackupBytes);
        Assert.Null(overview.Instance.OldestWaitingSeconds);

        var drive = Assert.Single(overview.Drives);
        Assert.Null(drive.ReadBytesPerSec);
        Assert.Null(drive.WriteBytesPerSec);
        Assert.Null(drive.Iops);
        Assert.Null(drive.BusyPercent);
        Assert.Null(drive.FullInDays);
        Assert.Equal("unknown", drive.Health);
        Assert.Null(drive.HealthNote);
    }

    [Fact]
    public async Task The_json_field_names_are_the_contract_the_screen_is_built_against()
    {
        var rig = new Rig();
        rig.Facts.Drives = [new DriveFacts("D:", "Downloads", "D:\\Downloads", 100, 50, 10, 12.5)];
        await rig.SampleAsync();
        var overview = await rig.Service.GetAsync(CancellationToken.None);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(overview, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var root = json.RootElement;

        Assert.Equal(["instance", "machine", "network", "drives", "history"], Names(root));
        Assert.Equal(
            [
                "version", "latest", "channel", "startedUtc", "address", "runsAs", "cpuPercent", "memoryBytes",
                "workersBusy", "oldestWaitingSeconds", "answerMs", "answerSlowestMs", "jobsToday", "jobsFailedToday",
                "requestsToday", "requestErrorsToday", "dataBytes", "databaseCount", "lastBackupUtc", "lastBackupBytes",
                "restartsThisWeek", "browsersLive"
            ],
            Names(root.GetProperty("instance")));
        Assert.Equal(
            [
                "os", "uptimeSeconds", "rebootPending", "cpuTemperatureC", "cpuPercent", "cores", "memoryUsedBytes",
                "memoryTotalBytes", "diskReadBytesPerSec", "diskWriteBytesPerSec", "diskIops"
            ],
            Names(root.GetProperty("machine")));
        Assert.Equal(
            [
                "adapter", "linkMbps", "ip", "inBytesPerSec", "outBytesPerSec", "internet", "dnsLookupMs", "nasMs",
                "nasName", "metadataMs", "clockInSync"
            ],
            Names(root.GetProperty("network")));
        Assert.Equal(
            [
                "name", "path", "totalBytes", "freeBytes", "delunoBytes", "fullInDays", "readBytesPerSec",
                "writeBytesPerSec", "iops", "busyPercent", "health", "healthNote"
            ],
            Names(root.GetProperty("drives")[0]));
        Assert.Equal(["cpu", "memory", "disk", "netIn"], Names(root.GetProperty("history")));
        Assert.Equal("D:\\Downloads", root.GetProperty("drives")[0].GetProperty("path").GetString());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("instance").GetProperty("cpuPercent").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("instance").GetProperty("startedUtc").ValueKind);
    }

    [Fact]
    public async Task The_instance_block_is_the_facts_plus_Delunos_own_load_and_the_requests_it_answered()
    {
        var rig = new Rig();
        rig.Latency.Record("/api/a", 10, 200);
        rig.Latency.Record("/api/b", 20, 200);
        rig.Latency.Record("/api/c", 90, 500);
        rig.Facts.Instance = rig.Facts.Instance with
        {
            WorkersBusy = 3,
            OldestWaitingSeconds = 42,
            JobsToday = 150,
            JobsFailedToday = 4,
            DataBytes = 5_000_000,
            DatabaseCount = 6,
            RestartsThisWeek = 2,
            BrowsersLive = 3,
            RunsAs = "docker",
            Address = "http://192.168.1.51:5099"
        };

        await rig.SampleAsync();
        await WaitUntilAsync(async () => (await rig.SampleAsync()).Instance.WorkersBusy == 3);
        var instance = (await rig.Service.GetAsync(CancellationToken.None)).Instance;

        Assert.Equal(3, instance.WorkersBusy);
        Assert.Equal(42, instance.OldestWaitingSeconds);
        Assert.Equal(150, instance.JobsToday);
        Assert.Equal(4, instance.JobsFailedToday);
        Assert.Equal(3, instance.RequestsToday);
        Assert.Equal(1, instance.RequestErrorsToday);
        Assert.Equal(20, instance.AnswerMs);
        Assert.Equal(90, instance.AnswerSlowestMs);
        Assert.Equal(200_000_000, instance.MemoryBytes);
        Assert.Equal(5_000_000, instance.DataBytes);
        Assert.Equal(6, instance.DatabaseCount);
        Assert.Equal(2, instance.RestartsThisWeek);
        Assert.Equal(3, instance.BrowsersLive);
        Assert.Equal("docker", instance.RunsAs);
        Assert.Equal("http://192.168.1.51:5099", instance.Address);
    }

    [Fact]
    public async Task An_overview_is_served_as_it_is_for_two_seconds_and_rebuilt_after()
    {
        var rig = new Rig();
        var first = await rig.SampleAsync();

        rig.Clock.Advance(TimeSpan.FromMilliseconds(1_500));
        Assert.Same(first, await rig.Service.GetAsync(CancellationToken.None));

        rig.Clock.Advance(TimeSpan.FromMilliseconds(600));
        Assert.NotSame(first, await rig.Service.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Readings_asked_for_in_quick_succession_are_taken_once()
    {
        var rig = new Rig();
        var first = await rig.SampleAsync();

        rig.Clock.Advance(TimeSpan.FromMilliseconds(300));
        var second = await rig.Service.SampleAsync(CancellationToken.None);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task The_history_fills_as_the_overview_is_sampled()
    {
        var rig = new Rig();
        rig.Host.Cpu = new CpuTimes(0, 1_000);
        for (var i = 0; i < 6; i++)
        {
            await rig.SampleAsync();
        }

        var history = (await rig.Service.GetAsync(CancellationToken.None)).History;

        Assert.InRange(history.Cpu.Count, 1, 3);
        Assert.Equal(history.Cpu.Count, history.Memory.Count);
        Assert.Equal(history.Cpu.Count, history.Disk.Count);
        Assert.Equal(history.Cpu.Count, history.NetIn.Count);
    }

    [Fact]
    public async Task While_nobody_watches_only_the_history_is_kept_and_nothing_slow_is_read()
    {
        var rig = new Rig();

        await rig.Service.SampleHistoryOnlyAsync(CancellationToken.None);

        Assert.Equal(0, rig.Facts.InstanceReads);
        Assert.Equal(0, rig.Facts.DriveReads);
        Assert.Null(rig.Probe.LookedUp);
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        var history = (await rig.Service.GetAsync(CancellationToken.None)).History;
        Assert.NotEmpty(history.Cpu);
    }

    private static string[] Names(JsonElement element) => element.EnumerateObject().Select(property => property.Name).ToArray();

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(30); // reason: poll interval inside a bounded wait (100 tries above)
        }

        Assert.Fail("The reading did not arrive.");
    }

    internal sealed class FakeFacts : ISystemOverviewFacts
    {
        public int InstanceReads { get; private set; }
        public int DriveReads { get; private set; }

        public InstanceFacts Instance { get; set; } = new(
            Version: "1.0.0-rc.99",
            Latest: true,
            Channel: "stable",
            StartedUtc: Start.AddHours(-5),
            Address: "http://192.168.1.51:5099",
            RunsAs: "app",
            WorkersBusy: 0,
            OldestWaitingSeconds: null,
            JobsToday: 0,
            JobsFailedToday: 0,
            DataBytes: 0,
            DatabaseCount: 6,
            LastBackupUtc: Start.AddDays(-1),
            LastBackupBytes: 1_234_567,
            RestartsThisWeek: 0,
            BrowsersLive: 1,
            Internet: true,
            NasHost: "nas",
            MetadataUrl: new Uri("https://metadata.example.test"));

        public IReadOnlyList<DriveFacts> Drives { get; set; } = [];

        public Task<InstanceFacts> ReadInstanceAsync(CancellationToken cancellationToken)
        {
            InstanceReads++;
            return Task.FromResult(Instance);
        }

        public Task<IReadOnlyList<DriveFacts>> ReadDrivesAsync(CancellationToken cancellationToken)
        {
            DriveReads++;
            return Task.FromResult(Drives);
        }
    }

    internal sealed class FakeProbe : INetworkProbe
    {
        public double? Lookup { get; set; }
        public double? Reach { get; set; }
        public HttpTiming? Http { get; set; }
        public string? LookedUp { get; private set; }

        public Task<double?> LookupMillisecondsAsync(string name, CancellationToken cancellationToken)
        {
            LookedUp = name;
            return Task.FromResult(Lookup);
        }

        public Task<double?> ReachMillisecondsAsync(string host, CancellationToken cancellationToken) => Task.FromResult(Reach);

        public Task<HttpTiming?> RequestAsync(Uri url, CancellationToken cancellationToken) => Task.FromResult(Http);
    }
}
