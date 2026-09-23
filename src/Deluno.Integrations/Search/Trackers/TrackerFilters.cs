using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// The filters a definition runs a value through (#606), with Cardigann's
/// meanings. A filter that cannot run throws; the caller decides whether the
/// field was optional. dateparse is the exception, as it is in Prowlarr: a
/// date it cannot read passes through untouched for the date field to try.
/// </summary>
public static class TrackerFilters
{
    private static readonly char[] ValidateDelimiters = [',', ' ', '/', ')', '(', '.', ';', '[', ']', '"', '|', ':'];

    public static string Apply(
        string value,
        IReadOnlyList<TrackerFilter> filters,
        IReadOnlyDictionary<string, object?> variables,
        TrackerContext context)
    {
        foreach (var filter in filters)
        {
            value = ApplyOne(value, filter, variables, context);
        }

        return value;
    }

    private static string ApplyOne(string data, TrackerFilter filter, IReadOnlyDictionary<string, object?> variables, TrackerContext context)
    {
        string Arg(int index) => index < filter.Args.Count ? filter.Args[index] : string.Empty;

        switch (filter.Name.ToLowerInvariant())
        {
            case "querystring":
                return QueryValue(data, Arg(0)) ?? string.Empty;

            case "dateparse":
            case "timeparse":
                return TrackerDates.TryParseLayout(data, Arg(0), out var parsed)
                    ? TrackerDates.Format(parsed)
                    : data;

            case "regexp":
            {
                var match = TrackerRegex.Get(Arg(0)).Match(data);
                return match.Groups.Count > 1 ? match.Groups[1].Value : string.Empty;
            }

            case "re_replace":
                return TrackerRegex.Replace(data, Arg(0), TrackerTemplate.Render(Arg(1), variables));

            case "split":
            {
                var separator = Arg(0);
                var parts = data.Split(separator.Length > 0 ? separator[0] : ' ');
                var position = int.Parse(Arg(1), CultureInfo.InvariantCulture);
                if (position < 0)
                {
                    position += parts.Length;
                }

                return position >= 0 && position < parts.Length
                    ? parts[position]
                    : throw new FormatException($"split has no part {Arg(1)} in '{data}'.");
            }

            case "replace":
                return Arg(0).Length == 0 ? data : data.Replace(Arg(0), TrackerTemplate.Render(Arg(1), variables), StringComparison.Ordinal);

            case "trim":
                return filter.Args.Count > 0 && Arg(0).Length > 0 ? data.Trim(Arg(0)[0]) : data.Trim();

            case "prepend":
                return TrackerTemplate.Render(Arg(0), variables) + data;

            case "append":
                return data + TrackerTemplate.Render(Arg(0), variables);

            case "tolower":
                return data.ToLowerInvariant();

            case "toupper":
                return data.ToUpperInvariant();

            case "urldecode":
                return WebUtility.UrlDecode(data);

            case "urlencode":
                return WebUtility.UrlEncode(data);

            case "htmldecode":
                return WebUtility.HtmlDecode(data);

            case "htmlencode":
                return WebUtility.HtmlEncode(data);

            case "timeago":
            case "reltime":
                return TrackerDates.Format(TrackerDates.FromTimeAgo(data, context.Now));

            case "fuzzytime":
                return TrackerDates.Format(TrackerDates.FromUnknown(data, context.Now));

            case "validfilename":
                return string.Concat(data.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

            case "diacritics":
                return RemoveDiacritics(data);

            case "validate":
            {
                var allowed = Arg(0).ToLowerInvariant().Split(ValidateDelimiters, StringSplitOptions.RemoveEmptyEntries);
                var present = data.ToLowerInvariant().Split(ValidateDelimiters, StringSplitOptions.RemoveEmptyEntries);
                return string.Join(",", allowed.Intersect(present));
            }

            case "jsonjoinarray":
                return TrackerJson.JoinArray(data, Arg(0), Arg(1));

            // Debugging aids in the format; they change nothing.
            case "hexdump":
            case "strdump":
                return data;

            default:
                throw new NotSupportedException($"The tracker filter '{filter.Name}' is not supported.");
        }
    }

    /// <summary>A query-string parameter's value from a URL or a bare query.</summary>
    public static string? QueryValue(string url, string name)
    {
        var query = url.Contains('?', StringComparison.Ordinal) ? url[(url.IndexOf('?', StringComparison.Ordinal) + 1)..] : url;
        var hash = query.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            query = query[..hash];
        }

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (string.Equals(WebUtility.UrlDecode(pair[0]), name, StringComparison.Ordinal))
            {
                return pair.Length > 1 ? WebUtility.UrlDecode(pair[1]) : string.Empty;
            }
        }

        return null;
    }

    public static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// The andmatch row filter: every word searched for appears in the title.
    /// A limit counts only that many characters of the search.
    /// </summary>
    public static bool MatchesAllWords(string title, string? keywords, int characterLimit = -1)
    {
        if (string.IsNullOrWhiteSpace(keywords))
        {
            return true;
        }

        var search = characterLimit > 0 && keywords.Length > characterLimit ? keywords[..characterLimit] : keywords;
        var words = Regex.Split(search.ToLowerInvariant(), @"[^\w]+").Where(word => word.Length > 0);
        var haystack = RemoveDiacritics(title).ToLowerInvariant();
        return words.All(word => haystack.Contains(RemoveDiacritics(word), StringComparison.Ordinal));
    }
}

/// <summary>What a definition reads that is not in the definition: the time, for now.</summary>
public sealed record TrackerContext(DateTimeOffset Now);

/// <summary>Numbers as trackers print them: "1,234", "1.5 GB", "3,2 Go".</summary>
public static class TrackerNumbers
{
    public static double? ParseDouble(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var digits = new string(text.Where(c => char.IsDigit(c) || c is '.' or ',').ToArray());
        if (digits.Length == 0)
        {
            return null;
        }

        // A comma and a dot: the last is the decimal point. A comma alone
        // followed by exactly three digits is a thousands separator.
        var lastComma = digits.LastIndexOf(',');
        var lastDot = digits.LastIndexOf('.');
        if (lastComma >= 0 && lastDot >= 0)
        {
            digits = lastComma > lastDot
                ? digits.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.')
                : digits.Replace(",", string.Empty, StringComparison.Ordinal);
        }
        else if (lastComma >= 0)
        {
            digits = digits.Count(c => c == ',') == 1 && digits.Length - lastComma - 1 != 3
                ? digits.Replace(',', '.')
                : digits.Replace(",", string.Empty, StringComparison.Ordinal);
        }

        if (digits.Count(c => c == '.') > 1)
        {
            var last = digits.LastIndexOf('.');
            digits = digits[..last].Replace(".", string.Empty, StringComparison.Ordinal) + digits[last..];
        }

        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    public static int? ParseInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var digits = new string(text.Where(char.IsDigit).ToArray());
        return digits.Length > 0 && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? (int)Math.Min(value, int.MaxValue)
            : text.Trim() == "-" ? 0 : null;
    }

    /// <summary>A size with its unit, in bytes. A bare number is bytes.</summary>
    public static long? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // The unit is the word after the number: "大小：970.3 MB" has other
        // letters in it, and they are not the unit.
        var sized = Regex.Match(text, @"(\d[\d.,\s]*)\s*([A-Za-zА-Яа-я]*)");
        var value = sized.Success ? ParseDouble(sized.Groups[1].Value) : null;
        if (value is null)
        {
            return null;
        }

        var unit = sized.Groups[2].Value.ToLowerInvariant();
        var multiplier = unit switch
        {
            _ when unit.StartsWith("tb", StringComparison.Ordinal) || unit.StartsWith("tib", StringComparison.Ordinal) || unit is "t" or "to" or "тб" => 1024d * 1024 * 1024 * 1024,
            _ when unit.StartsWith("gb", StringComparison.Ordinal) || unit.StartsWith("gib", StringComparison.Ordinal) || unit is "g" or "go" or "гб" => 1024d * 1024 * 1024,
            _ when unit.StartsWith("mb", StringComparison.Ordinal) || unit.StartsWith("mib", StringComparison.Ordinal) || unit is "m" or "mo" or "мб" => 1024d * 1024,
            _ when unit.StartsWith("kb", StringComparison.Ordinal) || unit.StartsWith("kib", StringComparison.Ordinal) || unit is "k" or "ko" or "кб" => 1024d,
            _ => 1d
        };

        return (long)(value.Value * multiplier);
    }

    /// <summary>An IMDb id as tt followed by at least seven digits, from a URL, a number or an id.</summary>
    public static string? ParseImdbId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Regex.Match(text, @"(?:tt)?(\d{5,10})", RegexOptions.IgnoreCase);
        return match.Success && long.TryParse(match.Groups[1].Value, out var number) && number > 0
            ? "tt" + number.ToString("D7", CultureInfo.InvariantCulture)
            : null;
    }
}
