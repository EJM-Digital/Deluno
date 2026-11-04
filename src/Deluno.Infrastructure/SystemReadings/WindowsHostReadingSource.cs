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
                    return _os = new OperatingSystemReading(WindowsReadingRules.PlainCaption(caption), item["Version"] as string);
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
                return WindowsReadingRules.HasContent(key?.GetValue(name));
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
                    result[WindowsReadingRules.Key(drive.RootDirectory.FullName)] = counters;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return WindowsReadingRules.SnapshotOf(result);
    }

    public string? VolumeKeyOf(string path) => WindowsReadingRules.VolumeKey(path, folder => Path.GetPathRoot(Path.GetFullPath(folder)));

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
                if (!WindowsReadingRules.IsDisk(item["DeviceId"]?.ToString(), disk.Value))
                {
                    continue;
                }

                return DriveHealthRules.Map(WindowsReadingRules.HealthCode(item["HealthStatus"]), WindowsReadingRules.OperationalCodes(item["OperationalStatus"]));
            }

            return PredictedFailure() ? WindowsReadingRules.PredictedToFail : DriveHealthReading.Unknown;
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
                && WindowsReadingRules.SameLetter(Convert.ToChar(value, CultureInfo.InvariantCulture), driveLetter)
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

    private static bool IsWmiFailure(Exception exception)
        => exception is ManagementException or UnauthorizedAccessException or COMException or InvalidCastException
            or InvalidOperationException or PlatformNotSupportedException or System.Security.SecurityException;
}

/// <summary>When Windows wants a restart, from the three places it records that.</summary>
