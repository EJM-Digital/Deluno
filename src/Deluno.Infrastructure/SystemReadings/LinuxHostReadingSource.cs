using System.Globalization;

namespace Deluno.Infrastructure.SystemReadings;

/// <summary>
/// The Linux and Docker readings, straight from /proc and /sys. Inside a container /proc still
/// describes the whole host, which is the machine Deluno runs on, so the readings are the host's.
/// Whatever the kernel does not expose here is null: a mounted network share or an overlay root has no
/// disk counters, and a drive's health needs SMART access Deluno does not have, so it stays unknown.
///
/// File access goes through <see cref="IProcSource"/> so every parser is tested against real
/// file contents without touching the machine running the tests.
/// </summary>
public sealed class LinuxHostReadingSource(IProcSource? source = null) : IHostReadingSource
{
    private readonly IProcSource _source = source ?? new ProcSource();

    public CpuTimes? ReadCpuTimes() => LinuxParsers.ParseCpuTimes(_source.ReadAllText("/proc/stat"));

    public (long Total, long Available)? ReadMemory() => LinuxParsers.ParseMemory(_source.ReadAllText("/proc/meminfo"));

    public long? ReadUptimeSeconds() => LinuxParsers.ParseUptimeSeconds(_source.ReadAllText("/proc/uptime"));

    public OperatingSystemReading? ReadOperatingSystem()
    {
        var caption = LinuxParsers.ParseOsPrettyName(_source.ReadAllText("/etc/os-release"));
        if (caption is null)
        {
            return null;
        }

        var kernel = _source.ReadAllText("/proc/sys/kernel/osrelease")?.Trim();
        return new OperatingSystemReading(caption, string.IsNullOrWhiteSpace(kernel) ? null : $"kernel {kernel}");
    }

    /// <summary>
    /// Debian and Ubuntu write /var/run/reboot-required when an update needs a restart. A machine without
    /// that mechanism (and any container, whose host's state it cannot see) gives null, not "no".
    /// </summary>
    public bool? ReadRebootPending()
    {
        if (_source.Exists("/var/run/reboot-required"))
        {
            return true;
        }

        return _source.Exists("/var/lib/dpkg") && !_source.Exists("/.dockerenv") ? false : null;
    }

    public double? ReadCpuTemperatureC()
    {
        var millidegrees = new List<long>();
        foreach (var zone in _source.ListDirectories("/sys/class/thermal"))
        {
            if (!Path.GetFileName(zone).StartsWith("thermal_zone", StringComparison.Ordinal))
            {
                continue;
            }

            var type = _source.ReadAllText($"{zone}/type")?.Trim();
            if (LinuxParsers.IsCpuSensor(type) && long.TryParse(_source.ReadAllText($"{zone}/temp")?.Trim(), CultureInfo.InvariantCulture, out var value))
            {
                millidegrees.Add(value);
            }
        }

        foreach (var hwmon in _source.ListDirectories("/sys/class/hwmon"))
        {
            var name = _source.ReadAllText($"{hwmon}/name")?.Trim();
            if (LinuxParsers.IsCpuSensor(name) && long.TryParse(_source.ReadAllText($"{hwmon}/temp1_input")?.Trim(), CultureInfo.InvariantCulture, out var value))
            {
                millidegrees.Add(value);
            }
        }

        return LinuxParsers.HottestCelsius(millidegrees);
    }

    public DiskSnapshot ReadDisks()
    {
        var uptimeMs = LinuxParsers.ParseUptimeSeconds(_source.ReadAllText("/proc/uptime")) is { } seconds ? seconds * 1000 : 0;
        var stats = LinuxParsers.ParseDiskStats(_source.ReadAllText("/proc/diskstats"), uptimeMs);
        if (stats.Count == 0)
        {
            return DiskSnapshot.Empty;
        }

        var wholeDisks = stats
            .Where(pair => _source.Exists($"/sys/block/{pair.Value.Name}") && LinuxParsers.IsPhysicalDiskName(pair.Value.Name))
            .Select(pair => pair.Value.Counters)
            .ToArray();

        return new DiskSnapshot(
            stats.ToDictionary(pair => pair.Key, pair => pair.Value.Counters, StringComparer.Ordinal),
            wholeDisks.Length == 0 ? null : DiskCounterMath.Sum(wholeDisks));
    }

    public string? VolumeKeyOf(string path)
        => LinuxParsers.DeviceOfPath(_source.ReadAllText("/proc/self/mountinfo"), path);

    public NetworkAdapterReading? ReadDefaultRouteAdapter() => DefaultRouteAdapter.Read();

    public DriveHealthReading ReadDriveHealth(string path) => DriveHealthReading.Unknown;
}

/// <summary>The few file reads the Linux source makes, so tests can serve real contents.</summary>
public interface IProcSource
{
    /// <summary>The file's text, or null when it does not exist or cannot be read.</summary>
    string? ReadAllText(string path);

    bool Exists(string path);

    IEnumerable<string> ListDirectories(string path);
}

public sealed class ProcSource : IProcSource
{
    public string? ReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public IEnumerable<string> ListDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>Parsers for the /proc and /sys files, each a pure function of the file's text.</summary>
public static class LinuxParsers
{
    private const int SectorBytes = 512;

    /// <summary>The first line of /proc/stat: idle is idle plus iowait, total is the first eight columns (guest time is already inside user).</summary>
    public static CpuTimes? ParseCpuTimes(string? text)
    {
        var line = text?.Split('\n').FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
            .Select(part => ulong.TryParse(part, CultureInfo.InvariantCulture, out var value) ? value : 0UL)
            .ToArray();
        if (parts.Length < 4)
        {
            return null;
        }

        var idle = parts[3] + (parts.Length > 4 ? parts[4] : 0UL);
        return new CpuTimes(idle, parts.Aggregate(0UL, (sum, value) => sum + value));
    }

    /// <summary>MemTotal and MemAvailable from /proc/meminfo, in bytes.</summary>
    public static (long Total, long Available)? ParseMemory(string? text)
    {
        long? total = null, available = null;
        foreach (var line in text?.Split('\n') ?? [])
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                total = KiloBytes(line);
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                available = KiloBytes(line);
            }
        }

        return total is > 0 && available is not null ? (total.Value, available.Value) : null;
    }

    private static long? KiloBytes(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var kilobytes) ? kilobytes * 1024 : null;
    }

    public static long? ParseUptimeSeconds(string? text)
    {
        var first = text?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? (long)seconds
            : null;
    }

    public static string? ParseOsPrettyName(string? text)
    {
        foreach (var line in text?.Split('\n') ?? [])
        {
            if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
            {
                var value = line["PRETTY_NAME=".Length..].Trim().Trim('"');
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    /// <summary>A thermal zone or hwmon chip that reports the processor's temperature.</summary>
    public static bool IsCpuSensor(string? type)
        => type is not null
           && (type.Contains("x86_pkg_temp", StringComparison.OrdinalIgnoreCase)
               || type.Contains("coretemp", StringComparison.OrdinalIgnoreCase)
               || type.Contains("k10temp", StringComparison.OrdinalIgnoreCase)
               || type.Contains("cpu", StringComparison.OrdinalIgnoreCase)
               || type.Contains("soc", StringComparison.OrdinalIgnoreCase));

    public static double? HottestCelsius(IEnumerable<long> millidegrees)
    {
        var celsius = millidegrees.Select(value => Math.Round(value / 1000d, 1)).Where(value => value is > 0 and < 150).ToArray();
        return celsius.Length == 0 ? null : celsius.Max();
    }

    /// <summary>A whole physical disk by name: not a loop, ram, device-mapper, md or zram device, which sit on top of one that is already counted.</summary>
    public static bool IsPhysicalDiskName(string name)
        => !(name.StartsWith("loop", StringComparison.Ordinal)
             || name.StartsWith("ram", StringComparison.Ordinal)
             || name.StartsWith("dm-", StringComparison.Ordinal)
             || name.StartsWith("md", StringComparison.Ordinal)
             || name.StartsWith("zram", StringComparison.Ordinal)
             || name.StartsWith("sr", StringComparison.Ordinal)
             || name.StartsWith("fd", StringComparison.Ordinal));

    /// <summary>
    /// /proc/diskstats keyed by "major:minor". Bytes are sectors of 512. Query time is the machine's uptime
    /// and idle time is that minus the time the disk spent doing I/O, so the share of a window that was
    /// not idle comes out the same way as on Windows.
    /// </summary>
    public static IReadOnlyDictionary<string, (string Name, DiskCounters Counters)> ParseDiskStats(string? text, long uptimeMs)
    {
        var result = new Dictionary<string, (string, DiskCounters)>(StringComparer.Ordinal);
        foreach (var line in text?.Split('\n') ?? [])
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 14)
            {
                continue;
            }

            long Field(int index) => long.TryParse(parts[index], CultureInfo.InvariantCulture, out var value) ? value : 0;

            var ioTicksMs = Field(12);
            result[$"{parts[0]}:{parts[1]}"] = (
                parts[2],
                new DiskCounters(
                    BytesRead: Field(5) * SectorBytes,
                    BytesWritten: Field(9) * SectorBytes,
                    Operations: Field(3) + Field(7),
                    IdleTime: Math.Max(0, uptimeMs - ioTicksMs),
                    QueryTime: uptimeMs));
        }

        return result;
    }

    /// <summary>
    /// The "major:minor" of the mount that holds <paramref name="path"/>: the mount point in
    /// /proc/self/mountinfo that is the longest prefix of the path.
    /// </summary>
    public static string? DeviceOfPath(string? mountInfo, string path)
    {
        if (string.IsNullOrWhiteSpace(mountInfo) || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Length > 1 ? path.TrimEnd('/') : path;
        string? best = null;
        var bestLength = -1;
        foreach (var line in mountInfo.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
            {
                continue;
            }

            var mountPoint = Unescape(parts[4]);
            var holds = mountPoint == "/" || normalized == mountPoint || normalized.StartsWith(mountPoint + "/", StringComparison.Ordinal);
            if (holds && mountPoint.Length > bestLength)
            {
                best = parts[2];
                bestLength = mountPoint.Length;
            }
        }

        return best;
    }

    /// <summary>mountinfo writes a space as \040 and a tab as \011 inside a path.</summary>
    private static string Unescape(string value) => value.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\134", "\\");
}
