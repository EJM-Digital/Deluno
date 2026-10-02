using System.Globalization;
using System.Text.RegularExpressions;

namespace Deluno.Contracts;

/// <summary>
/// A version number read from a connection or a release feed (#1117): its numeric parts, and whether it
/// is a beta, release candidate or development build.
/// </summary>
public sealed record ParsedVersion(IReadOnlyList<int> Parts, bool IsPreRelease);

/// <summary>
/// Compares the version a connection runs with the newest stable release of its project (#1117).
///
/// <para>Only the dotted numbers count. A leading "v", build suffixes such as "-ls123" or a Plex
/// "-e5521bd8c" hash, and "+build" metadata are ignored. A beta, release candidate or development
/// build is never called out of date, and a version equal to or newer than the latest stable is up
/// to date: Deluno names an update only when it is sure there is one.</para>
/// </summary>
public static partial class ReleaseVersions
{
    // After the numbers: a pre-release word ("-beta1", ".dev0", "rc2", "b1"), up to the next separator.
    [GeneratedRegex(@"^(?:[-._ ]?(?:alpha|beta|rc|development|dev|preview|pre|nightly|testing|snapshot)|[ab](?=\d))\d*(?:[-._+ ]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PreReleaseSuffix();

    [GeneratedRegex(@"^\d+(?:\.\d+)*", RegexOptions.CultureInvariant)]
    private static partial Regex NumericPrefix();

    /// <summary>The numbers of a version, or null when the text does not start with a number.</summary>
    public static ParsedVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        if (value.Length > 1 && (value[0] is 'v' or 'V') && char.IsDigit(value[1]))
        {
            value = value[1..];
        }

        var numbers = NumericPrefix().Match(value);
        if (!numbers.Success)
        {
            return null;
        }

        var parts = new List<int>();
        foreach (var piece in numbers.Value.Split('.'))
        {
            if (!int.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                // A part too large for a version number (a date stamp, a long run of digits) is not a version.
                return null;
            }

            parts.Add(number);
        }

        var rest = value[numbers.Length..];
        return new ParsedVersion(parts, rest.Length > 0 && PreReleaseSuffix().IsMatch(rest));
    }

    /// <summary>Compares the numbers only, missing parts counting as zero ("4.6" equals "4.6.0").</summary>
    public static int Compare(ParsedVersion left, ParsedVersion right)
    {
        var length = Math.Max(left.Parts.Count, right.Parts.Count);
        for (var index = 0; index < length; index++)
        {
            var a = index < left.Parts.Count ? left.Parts[index] : 0;
            var b = index < right.Parts.Count ? right.Parts[index] : 0;
            if (a != b)
            {
                return a.CompareTo(b);
            }
        }

        return 0;
    }

    /// <summary>
    /// True only when <paramref name="running"/> is a stable build older than <paramref name="latestStable"/>.
    /// False when either cannot be read, when the running build is a beta or development build, and when it
    /// equals or is newer than the latest stable release.
    /// </summary>
    public static bool IsOutOfDate(string? running, string? latestStable)
    {
        var current = Parse(running);
        var latest = Parse(latestStable);
        return current is { IsPreRelease: false } && latest is { IsPreRelease: false } && Compare(current, latest) < 0;
    }
}
