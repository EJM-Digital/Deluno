using System.Globalization;
using Deluno.Infrastructure.Storage;

namespace Deluno.Infrastructure.SystemReadings;

/// <summary>
/// The decisions the Windows reader makes about what Windows tells it, kept apart from the calls that ask
/// (WindowsHostReadingSource) so each can be tested on any system.
/// </summary>
public static class WindowsReadingRules
{
    public static readonly DriveHealthReading PredictedToFail = new("failing", "The drive expects to fail soon.");

    public static string PlainCaption(string caption) => caption.Replace("Microsoft ", string.Empty);

    public static string Key(string root) => root.TrimEnd((char)92, '/').ToUpperInvariant();

    /// <summary>A volume is its own counter on Windows, so the machine's disks are the volumes added together.</summary>
    public static DiskSnapshot SnapshotOf(IReadOnlyDictionary<string, DiskCounters> volumes)
        => new(volumes, volumes.Count == 0 ? null : DiskCounterMath.Sum(volumes.Values));

    /// <summary>The key of the drive a folder is on; null for a network share or anything that is not a drive letter.</summary>
    public static string? VolumeKey(string path, Func<string, string?> rootOf)
    {
        if (string.IsNullOrWhiteSpace(path) || VolumeSpace.IsNetworkSharePath(path))
        {
            return null;
        }

        try
        {
            var root = rootOf(path);
            return string.IsNullOrWhiteSpace(root) || !root.Contains(':') ? null : Key(root);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public static bool IsDisk(string? deviceId, int diskNumber)
        => string.Equals(deviceId, diskNumber.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    public static bool SameLetter(char first, char second)
        => char.ToUpperInvariant(first) == char.ToUpperInvariant(second);

    public static bool HasContent(object? value) => value is Array { Length: > 0 } or string { Length: > 0 };

    public static int HealthCode(object? value) => value is { } known ? Convert.ToInt32(known, CultureInfo.InvariantCulture) : -1;

    public static int[] OperationalCodes(object? value) => value is ushort[] raw ? raw.Select(code => (int)code).ToArray() : [];

}

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
