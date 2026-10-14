using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Deluno.Contracts;

/// <summary>
/// A version number read from a connection or a release feed (#1117): its numeric parts, and whether it
/// is a beta, release candidate or development build.
/// </summary>
/// <param name="PreReleaseLabel">The text after the numbers of a pre-release ("rc.2" for "1.0.0-rc.2"); null for a stable build.</param>
public sealed record ParsedVersion(IReadOnlyList<int> Parts, bool IsPreRelease, string? PreReleaseLabel = null);

/// <summary>
/// Compares the version a connection runs with the newest release of its project (#1117, #1123).
///
/// <para>Only the dotted numbers and a pre-release label count. A leading "v", build suffixes such as
/// "-ls123" or a Plex "-e5521bd8c" hash, and "+build" metadata are ignored. A stable build is compared
/// only with a stable release, so a beta of its project never nags it. A beta, release candidate or
/// development build is out of date when any newer release exists, rc or stable (1.0.0-rc.2 is newer
/// than 1.0.0-rc.1, and 1.0.0 is newer than 1.0.0-rc.9). A version equal to or newer than the latest is
/// up to date: Deluno names an update only when it is sure there is one.</para>
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
        var preRelease = rest.Length > 0 && PreReleaseSuffix().IsMatch(rest);
        string? label = null;
        if (preRelease)
        {
            label = rest.TrimStart('-', '.', '_', ' ');
            var plus = label.IndexOf('+');
            if (plus >= 0)
            {
                label = label[..plus];
            }
        }

        return new ParsedVersion(parts, preRelease, label);
    }

    [GeneratedRegex(@"\d+|[A-Za-z]+", RegexOptions.CultureInvariant)]
    private static partial Regex LabelToken();

    /// <summary>
    /// Version order, pre-releases included: the numbers first, then a stable build is newer than any
    /// pre-release of the same numbers, and pre-release labels compare piece by piece ("rc.2" over "rc.1";
    /// a number sorts below a word, as semver does).
    /// </summary>
    public static int ComparePrecedence(ParsedVersion left, ParsedVersion right)
    {
        var numbers = Compare(left, right);
        if (numbers != 0)
        {
            return numbers;
        }

        if (left.IsPreRelease != right.IsPreRelease)
        {
            return left.IsPreRelease ? -1 : 1;
        }

        if (!left.IsPreRelease)
        {
            return 0;
        }

        var a = LabelToken().Matches(left.PreReleaseLabel ?? string.Empty).Select(match => match.Value).ToArray();
        var b = LabelToken().Matches(right.PreReleaseLabel ?? string.Empty).Select(match => match.Value).ToArray();
        for (var index = 0; index < Math.Min(a.Length, b.Length); index++)
        {
            var x = a[index];
            var y = b[index];
            var xNumber = char.IsDigit(x[0]);
            var yNumber = char.IsDigit(y[0]);
            int result;
            if (xNumber && yNumber)
            {
                var xt = x.TrimStart('0');
                var yt = y.TrimStart('0');
                result = xt.Length != yt.Length ? xt.Length.CompareTo(yt.Length) : string.CompareOrdinal(xt, yt);
            }
            else if (xNumber != yNumber)
            {
                result = xNumber ? -1 : 1;
            }
            else
            {
                result = string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            }

            if (result != 0)
            {
                return result;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    /// <summary>
    /// The newest release in a GitHub <c>/releases</c> answer (a JSON array), pre-releases included and
    /// drafts never, by version order rather than by date (#1123). Null when none has a readable tag.
    /// Shared by the installer's component picker, Update now and the version check so they agree.
    /// </summary>
    /// <returns>A detached copy of the release object, safe to use after the call.</returns>
    public static JsonElement? NewestGitHubRelease(string releasesJson)
    {
        try
        {
            using var document = JsonDocument.Parse(releasesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            JsonElement? best = null;
            ParsedVersion? bestVersion = null;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                    || !item.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String
                    || Parse(tag.GetString()) is not { } version)
                {
                    continue;
                }

                if (bestVersion is null || ComparePrecedence(version, bestVersion) > 0)
                {
                    bestVersion = version;
                    best = item.Clone();
                }
            }

            return best;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The releases in a GitHub <c>/releases</c> answer, newest version first (not list order): drafts never, tags that are
    /// not a version never, and release candidates only when <paramref name="includePreReleases"/> is true. Used when the
    /// newest release is not complete yet (its files are still being uploaded) and the picker looks at the next one (#1263).
    /// </summary>
    /// <returns>Detached copies of the release objects.</returns>
    public static IReadOnlyList<JsonElement> GitHubReleasesNewestFirst(string releasesJson, bool includePreReleases)
    {
        try
        {
            using var document = JsonDocument.Parse(releasesJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var found = new List<(ParsedVersion Version, JsonElement Release)>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                    || !item.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String
                    || Parse(tag.GetString()) is not { } version)
                {
                    continue;
                }

                var flagged = item.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
                if (!includePreReleases && (flagged || version.IsPreRelease))
                {
                    continue;
                }

                found.Add((version, item.Clone()));
            }

            // A stable sort, so equal versions keep the order GitHub listed them in.
            return found.Select((entry, index) => (entry, index))
                .OrderByDescending(x => x.entry.Version, Comparer<ParsedVersion>.Create(ComparePrecedence))
                .ThenBy(x => x.index)
                .Select(x => x.entry.Release)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
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
    /// True when <paramref name="running"/> is older than <paramref name="latest"/>: a stable build is compared
    /// only with a stable release; a pre-release build with any release, rc or stable. False when either cannot
    /// be read, and when the running build equals or is newer than the latest.
    /// </summary>
    public static bool IsOutOfDate(string? running, string? latest)
    {
        var current = Parse(running);
        var newest = Parse(latest);
        if (current is null || newest is null)
        {
            return false;
        }

        return (current.IsPreRelease || !newest.IsPreRelease) && ComparePrecedence(current, newest) < 0;
    }
}
