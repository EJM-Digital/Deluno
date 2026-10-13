using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Deluno.Worker.HealthChecks.Machine;

/// <summary>
/// What the computer's power plan does on mains power, in seconds (0 means never). Null for a value the
/// computer does not report.
/// </summary>
public sealed record MachinePowerReading(int? SleepAfterSecondsOnAc, int? HibernateAfterSecondsOnAc);

/// <summary>Reads the computer's own power plan. Null when it cannot be read or does not apply (Linux, Docker).</summary>
public interface IMachinePowerReader
{
    MachinePowerReading? Read();
}

/// <summary>One physical drive's own report of its health (Windows: Get-PhysicalDisk HealthStatus).</summary>
public sealed record PhysicalDriveHealth(string Name, string Health, string? Operational, string? MediaType)
{
    /// <summary>Anything other than a plain "Healthy" is a warning the person should hear.</summary>
    public bool IsWarning => !string.Equals(Health, "Healthy", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Health, "Unknown", StringComparison.OrdinalIgnoreCase);

    public bool IsFailing => string.Equals(Health, "Unhealthy", StringComparison.OrdinalIgnoreCase);
}

public interface IMachineDriveHealthReader
{
    /// <summary>Null when the computer cannot say (Linux, Docker, no permission); never a guess.</summary>
    Task<IReadOnlyList<PhysicalDriveHealth>?> ReadAsync(CancellationToken cancellationToken);
}

public interface IMachineRebootReader
{
    /// <summary>True when the computer is waiting for a restart; null when it cannot be told.</summary>
    bool? RebootPending();
}

public interface IMachineTemperatureReader
{
    /// <summary>The processor's temperature in degrees Celsius, or null when the hardware does not report it.</summary>
    Task<double?> CpuCelsiusAsync(CancellationToken cancellationToken);
}

/// <summary>Reads the Windows power plan with the power API; no process is started.</summary>
public sealed class WindowsMachinePowerReader : IMachinePowerReader
{
    private static readonly Guid SleepSubgroup = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid StandbyIdle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    private static readonly Guid HibernateIdle = new("9d7815a6-7ee4-497e-8888-515a05f02364");

    public MachinePowerReading? Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var schemePointer) != 0 || schemePointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var scheme = Marshal.PtrToStructure<Guid>(schemePointer);
                return new MachinePowerReading(ReadAc(scheme, StandbyIdle), ReadAc(scheme, HibernateIdle));
            }
            finally
            {
                LocalFree(schemePointer);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return null;
        }
    }

    private static int? ReadAc(Guid scheme, Guid setting)
    {
        var subgroup = SleepSubgroup;
        var which = setting;
        return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref which, out var seconds) == 0
            ? (int)Math.Min(seconds, int.MaxValue)
            : null;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, out uint acValueIndex);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

/// <summary>Runs one short, read-only PowerShell command and returns what it printed, or null on any failure.</summary>
internal static class PowerShellRead
{
    public static async Task<string?> RunAsync(string script, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(exe);
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(script);
            // Both streams are read as they arrive, and a PowerShell that outlasts its time is stopped, not left running (#1258).
            var result = await Deluno.Infrastructure.Processes.ChildProcess.RunAsync(start, timeout, cancellationToken);
            return result.Started && !result.TimedOut && result.ExitCode == 0 ? result.Output : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}

/// <summary>Windows: each physical drive's own health report, the same one Storage settings shows.</summary>
public sealed class WindowsMachineDriveHealthReader : IMachineDriveHealthReader
{
    public async Task<IReadOnlyList<PhysicalDriveHealth>?> ReadAsync(CancellationToken cancellationToken)
    {
        var json = await PowerShellRead.RunAsync(
            "Get-PhysicalDisk | Select-Object FriendlyName,HealthStatus,OperationalStatus,MediaType | ConvertTo-Json -Compress",
            TimeSpan.FromSeconds(20),
            cancellationToken);
        return Parse(json);
    }

    /// <summary>PowerShell prints one object (not an array) for a single drive; both shapes are read.</summary>
    internal static IReadOnlyList<PhysicalDriveHealth>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var elements = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : [document.RootElement];
            var drives = new List<PhysicalDriveHealth>();
            foreach (var element in elements)
            {
                var health = Text(element, "HealthStatus");
                if (health is null)
                {
                    continue;
                }

                drives.Add(new PhysicalDriveHealth(Text(element, "FriendlyName") ?? "A drive", health, Text(element, "OperationalStatus"), Text(element, "MediaType")));
            }

            return drives;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            // Windows prints enums as numbers when it cannot name them: 0 Healthy, 1 Warning, 2 Unhealthy, 5 Unknown.
            JsonValueKind.Number when name == "HealthStatus" => value.GetInt32() switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" },
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }
}

/// <summary>Windows: the registry's own "restart needed" markers. Linux: the reboot-required flag file.</summary>
public sealed class MachineRebootReader : IMachineRebootReader
{
    public bool? RebootPending()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsRebootPending();
            }

            // Inside a container the flag file is the image's, not the host's: it cannot say, so it says nothing.
            if (!OperatingSystem.IsLinux() || string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return File.Exists("/var/run/reboot-required") || File.Exists("/run/reboot-required");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool WindowsRebootPending()
    {
        using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        foreach (var path in new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"
        })
        {
            using var key = hklm.OpenSubKey(path);
            if (key is not null)
            {
                return true;
            }
        }

        // A file rename queued for the next start is how installers ask for a restart; Windows Update's own
        // "updates waiting" marker above is the one that matters most, this is the long-standing second signal.
        using var session = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
        return session?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 };
    }
}

/// <summary>Windows: the thermal zone WMI reports (often admin only); Linux: /sys/class/thermal. Null when there is none.</summary>
public sealed class MachineTemperatureReader : IMachineTemperatureReader
{
    public async Task<double?> CpuCelsiusAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var text = await PowerShellRead.RunAsync(
                "(Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature -ErrorAction Stop | Measure-Object -Property CurrentTemperature -Maximum).Maximum",
                TimeSpan.FromSeconds(15),
                cancellationToken);
            return double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var tenthsOfKelvin) && tenthsOfKelvin > 0
                ? Math.Round(tenthsOfKelvin / 10d - 273.15d, 1)
                : null;
        }

        return ReadLinux("/sys/class/thermal");
    }

    internal static double? ReadLinux(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return null;
            }

            double? hottest = null;
            foreach (var zone in Directory.EnumerateDirectories(root, "thermal_zone*"))
            {
                var type = File.Exists(Path.Combine(zone, "type")) ? File.ReadAllText(Path.Combine(zone, "type")).Trim() : string.Empty;
                // Only the processor's own zones: a battery or wifi chip running warm is not the processor.
                if (!(type.Contains("cpu", StringComparison.OrdinalIgnoreCase) || type.Contains("x86_pkg", StringComparison.OrdinalIgnoreCase) || type.Contains("coretemp", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var path = Path.Combine(zone, "temp");
                if (File.Exists(path) && double.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var milli) && milli > 0)
                {
                    var celsius = milli / 1000d;
                    hottest = hottest is null ? celsius : Math.Max(hottest.Value, celsius);
                }
            }

            return hottest;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
