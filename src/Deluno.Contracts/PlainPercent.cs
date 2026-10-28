using System.Globalization;

namespace Deluno.Contracts;

/// <summary>
/// A share written as a person reads it, "50%" or "12.5%", whatever the machine's culture. The "P" format follows the
/// culture: the invariant one a Linux host and the Docker image run under writes "50 %", so the same sentence read
/// differently on Windows and in Docker (CI, 28 Oct).
/// </summary>
public static class PlainPercent
{
    /// <param name="share">From 0 to 1.</param>
    /// <param name="decimals">Places after the point; a trailing zero is kept, so 0.125 with one place is "12.5%" and 0.5 is "50.0%".</param>
    public static string Of(double share, int decimals = 0)
        => (share * 100).ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + "%";
}
