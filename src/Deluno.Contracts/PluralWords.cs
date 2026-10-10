using System.Globalization;

namespace Deluno.Contracts;

/// <summary>
/// A count with its noun in the right number, for the plain sentences Deluno writes (#1223):
/// "1 peer", "2 peers", "1 check". A sentence that put a number in front of a fixed plural read "1 peers".
/// </summary>
public static class PluralWords
{
    /// <summary>"1 peer" or "2 peers". Pass <paramref name="many"/> for a noun that does not just add an s ("1 entry", "2 entries").</summary>
    public static string Count(long count, string one, string? many = null)
        => $"{count.ToString(CultureInfo.InvariantCulture)} {Noun(count, one, many)}";

    /// <summary>The noun alone, in the right number for <paramref name="count"/>.</summary>
    public static string Noun(long count, string one, string? many = null)
        => count == 1 ? one : many ?? one + "s";

    /// <summary>A whole number of minutes, rounded, with its unit: "1 minute", "45 minutes".</summary>
    public static string Minutes(double minutes)
        => Count((long)Math.Round(minutes, MidpointRounding.AwayFromZero), "minute");
}
