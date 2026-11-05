using System.Globalization;

namespace Deluno.Contracts;

/// <summary>
/// The one place a speed becomes words in Activity and notifications (#1489), the same rule as the web app's
/// <c>format-speed.ts</c> and Weir's: under 1 MB/s whole KB/s, from 1 MB/s MB/s (one decimal under 10, whole above),
/// from 1000 MB/s GB/s with one decimal. Speeds are MB/s of 1,000,000 bytes, as every client's bytes per second are
/// divided into <c>SpeedMbps</c>, so 1 MB/s is 1000 KB/s.
/// </summary>
public static class SpeedText
{
    private const double Step = 1000d;

    /// <summary>"120 KB/s", "3.4 MB/s", "42 MB/s", "1.1 GB/s"; 0 and anything unreadable read "0 KB/s".</summary>
    public static string FromMbps(double mbps)
    {
        var value = double.IsFinite(mbps) ? Math.Max(0d, mbps) : 0d;
        var culture = CultureInfo.InvariantCulture;
        var kilobytes = Math.Round(value * Step, MidpointRounding.AwayFromZero);
        if (kilobytes < Step)
        {
            return kilobytes.ToString("0", culture) + " KB/s";
        }

        var wholeMegabytes = Math.Round(value, MidpointRounding.AwayFromZero);
        if (value < Step && wholeMegabytes < Step)
        {
            var tenths = Math.Round(value * 10d, MidpointRounding.AwayFromZero) / 10d;
            return (tenths >= 10d ? wholeMegabytes.ToString("0", culture) : tenths.ToString("0.0", culture)) + " MB/s";
        }

        var gigabytes = Math.Round(value / Step * 10d, MidpointRounding.AwayFromZero) / 10d;
        return gigabytes.ToString("0.0", culture) + " GB/s";
    }

    /// <summary>The same, from a speed in KB/s (a Too slow limit is saved in KB/s).</summary>
    public static string FromKbps(double kbps) => FromMbps(kbps / Step);
}
