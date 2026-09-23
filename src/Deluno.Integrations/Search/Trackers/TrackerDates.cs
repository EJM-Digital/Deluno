using System.Globalization;
using System.Text.RegularExpressions;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// Dates as trackers print them (#606): exact layouts, "3 hours ago", "today
/// 12:25am", Unix seconds. Every result is a DateTimeOffset; one with no
/// offset is taken as UTC.
/// </summary>
public static partial class TrackerDates
{
    private const string FormatPattern = "ddd, dd MMM yyyy HH':'mm':'ss zzz";

    public static string Format(DateTimeOffset value) => value.ToString(FormatPattern, CultureInfo.InvariantCulture);

    /// <summary>The dateparse filter: a .NET layout, as the definitions write it.</summary>
    public static bool TryParseLayout(string text, string layout, out DateTimeOffset value)
    {
        var normalized = Spaces().Replace(text.Trim(), " ");
        return DateTimeOffset.TryParseExact(
            normalized,
            layout,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out value);
    }

    public static DateTimeOffset FromTimeAgo(string text, DateTimeOffset now)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("now", StringComparison.Ordinal))
        {
            return now;
        }

        lower = lower.Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("ago", string.Empty, StringComparison.Ordinal)
            .Replace("and", string.Empty, StringComparison.Ordinal);

        var elapsed = TimeSpan.Zero;
        var any = false;
        foreach (Match match in TimeAgoPart().Matches(lower))
        {
            any = true;
            var amount = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var unit = match.Groups[2].Value;
            elapsed += unit switch
            {
                _ when unit.Contains("sec", StringComparison.Ordinal) || unit == "s" => TimeSpan.FromSeconds(amount),
                _ when unit.Contains("min", StringComparison.Ordinal) || unit == "m" => TimeSpan.FromMinutes(amount),
                _ when unit.Contains("hour", StringComparison.Ordinal) || unit.Contains("hr", StringComparison.Ordinal) || unit == "h" => TimeSpan.FromHours(amount),
                _ when unit.Contains("day", StringComparison.Ordinal) || unit == "d" => TimeSpan.FromDays(amount),
                _ when unit.Contains("week", StringComparison.Ordinal) || unit.Contains("wk", StringComparison.Ordinal) || unit == "w" => TimeSpan.FromDays(amount * 7),
                _ when unit.Contains("month", StringComparison.Ordinal) || unit == "mo" => TimeSpan.FromDays(amount * 30),
                _ when unit.Contains("year", StringComparison.Ordinal) || unit == "y" => TimeSpan.FromDays(amount * 365),
                _ => throw new FormatException($"'{text}' has a unit of time Deluno does not know: {unit}.")
            };
        }

        return any ? now - elapsed : throw new FormatException($"'{text}' is not a time ago.");
    }

    /// <summary>Anything a tracker might print as a date. Throws when none of the readings fit.</summary>
    public static DateTimeOffset FromUnknown(string text, DateTimeOffset now)
    {
        var trimmed = Spaces().Replace(text.Trim(), " ");
        var lower = trimmed.ToLowerInvariant();
        if (lower.Length == 0)
        {
            throw new FormatException("There is no date to read.");
        }

        if (lower is "now" or "just now" || lower.Contains("now", StringComparison.Ordinal) && lower.Length < 12)
        {
            return now;
        }

        // Unix time, in seconds or milliseconds.
        if (UnixTime().IsMatch(lower) && long.TryParse(lower, NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
        {
            return lower.Length >= 13 ? DateTimeOffset.FromUnixTimeMilliseconds(unix) : DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        if (lower.Contains("ago", StringComparison.Ordinal))
        {
            return FromTimeAgo(lower, now);
        }

        var relative = RelativeDay().Match(lower);
        if (relative.Success)
        {
            var day = relative.Groups[1].Value switch
            {
                "yesterday" => now.AddDays(-1),
                "tomorrow" => now.AddDays(1),
                _ => now
            };
            var rest = lower[relative.Length..].Replace("at", string.Empty, StringComparison.Ordinal).Trim(' ', ',', '-');
            return rest.Length > 0 && TryTimeOfDay(rest, out var time)
                ? new DateTimeOffset(day.Date + time, day.Offset)
                : new DateTimeOffset(day.Date, day.Offset);
        }

        // A time alone is today, as a listing sorted by time shows its newest.
        if (TryTimeOfDay(lower, out var today))
        {
            return new DateTimeOffset(now.Date + today, now.Offset);
        }

        var withoutOrdinals = Ordinal().Replace(trimmed, "$1");
        foreach (var candidate in new[] { trimmed, withoutOrdinals })
        {
            if (DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed;
            }
        }

        if (TimeAgoPart().IsMatch(lower))
        {
            return FromTimeAgo(lower, now);
        }

        throw new FormatException($"'{text}' is not a date Deluno can read.");
    }

    private static bool TryTimeOfDay(string text, out TimeSpan time)
    {
        var compact = text.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        string[] layouts = ["h:mmtt", "hh:mmtt", "htt", "hhtt", "H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss", "h:mm:sstt"];
        if (DateTime.TryParseExact(compact.ToUpperInvariant(), layouts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            time = parsed.TimeOfDay;
            return true;
        }

        time = default;
        return false;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s*?([\d\.]+)\s*?([^\d\s\.]+)\s*?")]
    private static partial Regex TimeAgoPart();

    [GeneratedRegex(@"^\d{9,13}$")]
    private static partial Regex UnixTime();

    [GeneratedRegex(@"^(today|yesterday|tomorrow)")]
    private static partial Regex RelativeDay();

    [GeneratedRegex(@"(\d)(st|nd|rd|th)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Ordinal();
}
