using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Deluno.Infrastructure.Storage;
using Microsoft.Win32;

namespace Deluno.Infrastructure.SystemReadings;

/// <summary>
/// The Windows readings: kernel calls for load, memory and disk counters, the registry for a pending
/// reboot, and WMI for the operating system's name, the CPU temperature and a drive's health. WMI is
/// only used for what has no cheaper source, and the slow ones are cached by the caller.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHostReadingSource : IHostReadingSource
{
    private OperatingSystemReading? _os;

    public CpuTimes? ReadCpuTimes() => WindowsNative.TryReadCpuTimes();

    public (long Total, long Available)? ReadMemory() => WindowsNative.TryReadMemory();

    public long? ReadUptimeSeconds() => Environment.TickCount64 / 1000;

    public OperatingSystemReading? ReadOperatingSystem()
    {
        if (_os is not null)
        {
            return _os;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Caption, Version FROM Win32_OperatingSystem");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                var caption = (item["Caption"] as string)?.Trim();
                if (!string.IsNullOrWhiteSpace(caption))
                {
                    return _os = new OperatingSystemReading(caption.Replace("Microsoft ", string.Empty), item["Version"] as string);
                }
            }
        }
        catch (Exception exception) when (IsWmiFailure(exception))
        {
        }

        return _os = new OperatingSystemReading(RuntimeInformation.OSDescription, null);
    }

    public bool? ReadRebootPending()
    {
        try
        {
            using var local = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            bool KeyExists(string path)
            {
                using var key = local.OpenSubKey(path);
                return key is not null;
            }

            bool HasValue(string path, string name)
            {
                using var key = local.OpenSubKey(path);
                return key?.GetValue(name) is Array { Length: > 0 } or string { Length: > 0 };
            }

            return RebootPendingRules.Evaluate(
                KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"),
                KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"),
                HasValue(@"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations"));
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>The hottest thermal zone the firmware reports. Many machines report none, or only to an administrator: null then.</summary>
    public double? ReadCpuTemperatureC()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            var tenthsKelvin = new List<double>();
            foreach (ManagementBaseObject item in searcher.Get())
            {
                if (item["CurrentTemperature"] is { } value)
                {
                    tenthsKelvin.Add(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                }
            }

            return ThermalZoneRules.HottestCelsius(tenthsKelvin);
        }
        catch (Exception exception) when (IsWmiFailure(exception))
        {
            return null;
        }
    }

    public DiskSnapshot ReadDisks()
    {
        var result = new Dictionary<string, DiskCounters>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                if (WindowsNative.TryReadVolumeCounters(drive.RootDirectory.FullName) is { } counters)
                {
                    result[Key(drive.RootDirectory.FullName)] = counters;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        // A volume is its own counter on Windows, so the machine's disks are the volumes added together.
        return new DiskSnapshot(result, result.Count == 0 ? null : DiskCounterMath.Sum(result.Values));
    }

    public string? VolumeKeyOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || VolumeSpace.IsNetworkSharePath(path))
        {
            return null;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrWhiteSpace(root) || !root.Contains(':') ? null : Key(root);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public NetworkAdapterReading? ReadDefaultRouteAdapter() => DefaultRouteAdapter.Read();

    public DriveHealthReading ReadDriveHealth(string path)
    {
        var key = VolumeKeyOf(path);
        if (key is null)
        {
            return DriveHealthReading.Unknown;
        }

        try
        {
            var disk = DiskNumberOf(key[0]);
            if (disk is null)
            {
                return DriveHealthReading.Unknown;
            }

            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT DeviceId, HealthStatus, OperationalStatus FROM MSFT_PhysicalDisk");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                if (!string.Equals(item["DeviceId"]?.ToString(), disk.Value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                {
                    continue;
                }

                var health = item["HealthStatus"] is { } h ? Convert.ToInt32(h, CultureInfo.InvariantCulture) : -1;
                var states = item["OperationalStatus"] is ushort[] raw ? raw.Select(value => (int)value).ToArray() : [];
                return DriveHealthRules.Map(health, states);
            }

            return PredictedFailure() ? new DriveHealthReading("failing", "The drive expects to fail soon.") : DriveHealthReading.Unknown;
        }
        catch (Exception exception) when (IsWmiFailure(exception))
        {
            return DriveHealthReading.Unknown;
        }
    }

    private static int? DiskNumberOf(char driveLetter)
    {
        using var searcher = new ManagementObjectSearcher(
            @"root\Microsoft\Windows\Storage",
            "SELECT DriveLetter, DiskNumber FROM MSFT_Partition");
        foreach (ManagementBaseObject item in searcher.Get())
        {
            if (item["DriveLetter"] is { } value
                && char.ToUpperInvariant(Convert.ToChar(value, CultureInfo.InvariantCulture)) == char.ToUpperInvariant(driveLetter)
                && item["DiskNumber"] is { } number)
            {
                return Convert.ToInt32(number, CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    /// <summary>The drive's own failure prediction (SMART), for a disk Windows' storage classes do not describe.</summary>
    private static bool PredictedFailure()
    {
        using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT PredictFailure FROM MSStorageDriver_FailurePredictStatus");
        foreach (ManagementBaseObject item in searcher.Get())
        {
            if (item["PredictFailure"] is true)
            {
                return true;
            }
        }

        return false;
    }

    private static string Key(string root) => root.TrimEnd('\\', '/').ToUpperInvariant();

    private static bool IsWmiFailure(Exception exception)
        => exception is ManagementException or UnauthorizedAccessException or COMException or InvalidCastException
            or InvalidOperationException or PlatformNotSupportedException or System.Security.SecurityException;
}

/// <summary>When Windows wants a restart, from the three places it records that.</summary>
public static class RebootPendingRules
{
    public static bool Evaluate(bool componentServicingPending, bool windowsUpdateRebootRequired, bool pendingFileRenames)
        => componentServicingPending || windowsUpdateRebootRequired || pendingFileRenames;
}

public static class ThermalZoneRules
{
    /// <summary>The hottest plausible reading, from tenths of a kelvin (what ACPI reports), or null when there is none.</summary>
    public static double? HottestCelsius(IEnumerable<double> tenthsKelvin)
    {
        var celsius = tenthsKelvin
            .Select(value => Math.Round(value / 10d - 273.15d, 1))
            .Where(value => value is > 0 and < 150)
            .ToArray();
        return celsius.Length == 0 ? null : celsius.Max();
    }
}

public static class DriveHealthRules
{
    /// <summary>
    /// Maps Windows' MSFT_PhysicalDisk HealthStatus (0 healthy, 1 warning, 2 unhealthy, anything else unknown)
    /// and its OperationalStatus codes to Deluno's four states and a plain sentence for anything but good.
    /// </summary>
    public static DriveHealthReading Map(int healthStatus, IReadOnlyCollection<int> operationalStatus)
    {
        var note = operationalStatus.Select(Describe).FirstOrDefault(text => text is not null);
        return healthStatus switch
        {
            0 => new DriveHealthReading("good", null),
            1 => new DriveHealthReading("warning", note ?? "The drive reports a warning."),
            2 => new DriveHealthReading("failing", note ?? "The drive reports that it is failing."),
            _ => DriveHealthReading.Unknown
        };
    }

    private static string? Describe(int code) => code switch
    {
        3 => "The drive is running in a reduced state.",
        5 => "The drive expects to fail soon.",
        6 => "The drive has an error.",
        10 => "The drive has stopped.",
        12 => "Windows lost contact with the drive.",
        13 => "The drive is out of space to repair itself.",
        _ => null
    };
}
