using System.Text.Json;
using System.Text.RegularExpressions;
using Deluno.Platform.Contracts;

namespace Deluno.Integrations.Search;

/// <summary>
/// Evaluates a release name (and optional inferred attributes) against the
/// structured conditions stored on a <see cref="CustomFormatItem"/>.
///
/// Conditions are stored as a JSON array in <c>CustomFormatItem.Conditions</c>:
/// <code>
/// [
///   { "type": "releaseTitle", "value": "DoVi" },
///   { "type": "resolution",   "value": "2160p" },
///   { "type": "source",       "value": "BluRay" }
/// ]
/// </code>
/// Supported condition types:
/// <list type="bullet">
/// <item><c>releaseTitle</c> — substring or regex match on the release name</item>
/// <item><c>source</c>       — BluRay | WEB | WEBRip | HDTV | Remux</item>
/// <item><c>resolution</c>   — 720p | 1080p | 2160p</item>
/// <item><c>hdr</c>          — HDR | HDR10 | DV | DolbyVision | HLG</item>
/// <item><c>codec</c>        — x265 | HEVC | x264 | AV1</item>
/// <item><c>releaseGroup</c> — substring match on the inferred release group</item>
/// <item><c>language</c>     — substring match: multi | dubbed | subbed | english | french …</item>
/// </list>
/// All conditions within a format must match for the format to be applied
/// (logical AND).  A legacy plain-text conditions field (no JSON) falls back
/// to the old substring-match behaviour so existing saved formats keep working.
/// </summary>
public static partial class CustomFormatMatcher
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Evaluates all supplied custom formats against <paramref name="releaseName"/>
    /// and returns the total score and list of matched format names.
    /// </summary>
    public static int Evaluate(
        string releaseName,
        IReadOnlyList<CustomFormatItem>? formats,
        out IReadOnlyList<CustomFormatMatchResult> matched)
    {
        if (formats is null || formats.Count == 0)
        {
            matched = [];
            return 0;
        }

        var results = new List<CustomFormatMatchResult>();
        var totalScore = 0;
        var lower = releaseName.ToLowerInvariant();
        var inferredGroup = InferReleaseGroup(releaseName);

        foreach (var format in formats)
        {
            var conditions = ParseConditions(format.Conditions);
            var (isMatch, matchedConditions, missedConditions) = EvaluateConditions(
                releaseName, lower, inferredGroup, conditions);

            if (!isMatch)
                continue;

            totalScore += format.Score;
            results.Add(new CustomFormatMatchResult(
                FormatId: format.Id,
                FormatName: format.Name,
                Score: format.Score,
                MatchedConditions: matchedConditions,
                MissedConditions: []));
        }

        matched = results;
        return totalScore;
    }

    /// <summary>
    /// Dry-run — evaluates all formats and returns per-condition hit/miss detail
    /// for every format regardless of whether it matched overall.
    /// Intended for the UI explanation panel.
    /// </summary>
    public static IReadOnlyList<CustomFormatDryRunResult> DryRun(
        string releaseName,
        IReadOnlyList<CustomFormatItem> formats)
    {
        var results = new List<CustomFormatDryRunResult>();
        var lower = releaseName.ToLowerInvariant();
        var inferredGroup = InferReleaseGroup(releaseName);

        foreach (var format in formats)
        {
            var conditions = ParseConditions(format.Conditions);
            var (isMatch, matchedConditions, missedConditions) = EvaluateConditions(
                releaseName, lower, inferredGroup, conditions);

            results.Add(new CustomFormatDryRunResult(
                FormatId: format.Id,
                FormatName: format.Name,
                Score: format.Score,
                IsMatch: isMatch,
                MatchedConditions: matchedConditions,
                MissedConditions: missedConditions));
        }

        return results;
    }

    // ── Condition parsing ──────────────────────────────────────────────────

    private static IReadOnlyList<CustomFormatCondition> ParseConditions(string? rawConditions)
    {
        if (string.IsNullOrWhiteSpace(rawConditions))
            return [];

        var trimmed = rawConditions.Trim();

        // JSON array path
        if (trimmed.StartsWith('['))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<CustomFormatCondition[]>(trimmed, JsonOptions);
                return parsed ?? [];
            }
            catch
            {
                return [];
            }
        }

        // Legacy plain-text "type:value" (single condition, backward-compat)
        var colonIdx = trimmed.IndexOf(':');
        if (colonIdx > 0 && colonIdx < trimmed.Length - 1)
        {
            return
            [
                new CustomFormatCondition(
                    Type: trimmed[..colonIdx].Trim(),
                    Value: trimmed[(colonIdx + 1)..].Trim(),
                    Negate: false,
                    Required: true)
            ];
        }

        // Bare token — treat as releaseTitle substring
        return [new CustomFormatCondition(Type: "releaseTitle", Value: trimmed, Negate: false, Required: true)];
    }

    // ── Condition evaluation ───────────────────────────────────────────────

    private static (bool IsMatch, string[] Matched, string[] Missed) EvaluateConditions(
        string releaseName,
        string lower,
        string? inferredGroup,
        IReadOnlyList<CustomFormatCondition> conditions)
    {
        if (conditions.Count == 0)
            return (false, [], ["No conditions defined"]);

        var matched = new List<string>();
        var missed = new List<string>();

        foreach (var condition in conditions)
        {
            var hit = EvaluateSingleCondition(releaseName, lower, inferredGroup, condition);
            var effectiveHit = condition.Negate ? !hit : hit;
            var label = $"{condition.Type}:{condition.Value}";
            if (condition.Negate) label = "NOT " + label;

            if (effectiveHit)
                matched.Add(label);
            else
                missed.Add(label);
        }

        // All conditions must match (AND semantics)
        var allMatch = missed.Count == 0;
        return (allMatch, matched.ToArray(), missed.ToArray());
    }

    private static bool EvaluateSingleCondition(
        string releaseName,
        string lower,
        string? inferredGroup,
        CustomFormatCondition condition)
    {
        var val = condition.Value?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(val)) return false;

        return condition.Type?.ToLowerInvariant() switch
        {
            "releasetitle" => MatchReleaseTitle(releaseName, lower, val),
            "source"       => MatchSource(lower, val),
            "resolution"   => MatchResolution(lower, val),
            "hdr"          => MatchHdr(lower, val),
            "codec"        => MatchCodec(lower, val),
            "releasegroup" => MatchReleaseGroup(inferredGroup, val),
            "language"     => MatchLanguage(lower, val),
            _              => lower.Contains(val.ToLowerInvariant(), StringComparison.Ordinal)
        };
    }

    // ── Per-type matchers ──────────────────────────────────────────────────

    private static bool MatchReleaseTitle(string releaseName, string lower, string pattern)
    {
        // If the pattern looks like a regex (contains meta chars), try regex
        if (pattern.AsSpan().ContainsAny(['*', '+', '?', '(', '|', '[', '\\', '^', '$']))
        {
            try
            {
                return Regex.IsMatch(releaseName, pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(50));
            }
            catch
            {
                // Fall through to substring
            }
        }

        return lower.Contains(pattern.ToLowerInvariant(), StringComparison.Ordinal);
    }

    private static bool MatchSource(string lower, string value)
    {
        var v = value.ToLowerInvariant();
        return v switch
        {
            "bluray"  => lower.Contains("bluray", StringComparison.Ordinal) || lower.Contains("blu-ray", StringComparison.Ordinal),
            "remux"   => lower.Contains("remux", StringComparison.Ordinal),
            "web"     => lower.Contains("web-dl", StringComparison.Ordinal) || lower.Contains("webdl", StringComparison.Ordinal) || (lower.Contains("web", StringComparison.Ordinal) && !lower.Contains("webrip", StringComparison.Ordinal)),
            "webrip"  => lower.Contains("webrip", StringComparison.Ordinal),
            "hdtv"    => lower.Contains("hdtv", StringComparison.Ordinal),
            "dvd"     => lower.Contains("dvdrip", StringComparison.Ordinal) || lower.Contains("dvd", StringComparison.Ordinal),
            _         => lower.Contains(v, StringComparison.Ordinal)
        };
    }

    private static bool MatchResolution(string lower, string value)
    {
        var v = value.ToLowerInvariant();
        return v switch
        {
            "2160p" => lower.Contains("2160", StringComparison.Ordinal) || lower.Contains("4k", StringComparison.Ordinal),
            "1080p" => lower.Contains("1080", StringComparison.Ordinal),
            "720p"  => lower.Contains("720", StringComparison.Ordinal),
            "480p"  => lower.Contains("480", StringComparison.Ordinal),
            _       => lower.Contains(v, StringComparison.Ordinal)
        };
    }

    private static bool MatchHdr(string lower, string value)
    {
        var v = value.ToLowerInvariant();
        return v switch
        {
            "dv" or "dolbyvision" => lower.Contains(".dv.", StringComparison.Ordinal) || lower.Contains("dolby.vision", StringComparison.Ordinal) || lower.Contains("dv.", StringComparison.Ordinal) || lower.Contains(".dovi", StringComparison.Ordinal),
            "hdr10plus" or "hdr10+" => lower.Contains("hdr10+", StringComparison.Ordinal) || lower.Contains("hdr10plus", StringComparison.Ordinal),
            "hdr10" or "hdr"    => lower.Contains("hdr10", StringComparison.Ordinal) || lower.Contains(".hdr.", StringComparison.Ordinal),
            "hlg"               => lower.Contains("hlg", StringComparison.Ordinal),
            _                   => lower.Contains(v, StringComparison.Ordinal)
        };
    }

    private static bool MatchCodec(string lower, string value)
    {
        var v = value.ToLowerInvariant();
        return v switch
        {
            "x265" or "hevc" => lower.Contains("x265", StringComparison.Ordinal) || lower.Contains("h265", StringComparison.Ordinal) || lower.Contains("hevc", StringComparison.Ordinal),
            "x264" or "avc"  => lower.Contains("x264", StringComparison.Ordinal) || lower.Contains("h264", StringComparison.Ordinal) || lower.Contains("avc", StringComparison.Ordinal),
            "av1"            => lower.Contains("av1", StringComparison.Ordinal),
            "xvid" or "divx" => lower.Contains("xvid", StringComparison.Ordinal) || lower.Contains("divx", StringComparison.Ordinal),
            _                => lower.Contains(v, StringComparison.Ordinal)
        };
    }

    private static bool MatchReleaseGroup(string? inferredGroup, string value)
    {
        if (string.IsNullOrWhiteSpace(inferredGroup)) return false;
        return inferredGroup.Contains(value.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchLanguage(string lower, string value)
        => lower.Contains(value.ToLowerInvariant(), StringComparison.Ordinal);

    private static string? InferReleaseGroup(string releaseName)
    {
        var match = ReleaseGroupRegex().Match(releaseName);
        return match.Success ? match.Groups["group"].Value : null;
    }

    [GeneratedRegex(@"-(?<group>[A-Za-z0-9]{2,20})$")]
    private static partial Regex ReleaseGroupRegex();
}

// ── Result models ──────────────────────────────────────────────────────────

/// <summary>Per-format match result for the scoring path.</summary>
public sealed record CustomFormatMatchResult(
    string FormatId,
    string FormatName,
    int Score,
    IReadOnlyList<string> MatchedConditions,
    IReadOnlyList<string> MissedConditions);

/// <summary>Per-format dry-run result for the UI explanation panel.</summary>
public sealed record CustomFormatDryRunResult(
    string FormatId,
    string FormatName,
    int Score,
    bool IsMatch,
    IReadOnlyList<string> MatchedConditions,
    IReadOnlyList<string> MissedConditions);

/// <summary>Single condition within a custom format's condition array.</summary>
public sealed record CustomFormatCondition(
    string Type,
    string Value,
    bool Negate = false,
    bool Required = true);
