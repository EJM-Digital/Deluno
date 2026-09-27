using Deluno.Platform.Contracts;

namespace Deluno.Recovery.Policies;

/// <summary>
/// What a download is, for the seeding-rule matrix to match against (#820):
/// its category, the host of the tracker it came from, and that source's own
/// tags. Deliberately the same three axes Cleanuparr's Seeding Rules use.
/// </summary>
public sealed record SharingRuleMatchContext(string? Category, string? TrackerHost, IReadOnlyList<string>? Tags);

/// <summary>
/// Picks the first enabled rule, in priority order, whose category/tracker/tag
/// criteria all match. Falls back to nothing (the caller then uses today's
/// single global policy) when no rule matches — this is deliberately never a
/// substitute for the global policy, only a set of exceptions to it.
/// </summary>
public static class SharingRuleMatcher
{
    public static SharingRuleItem? FindMatch(IReadOnlyList<SharingRuleItem> rules, SharingRuleMatchContext context)
    {
        foreach (var rule in rules.Where(rule => rule.IsEnabled).OrderBy(rule => rule.Priority))
        {
            if (Matches(rule, context))
            {
                return rule;
            }
        }

        return null;
    }

    private static bool Matches(SharingRuleItem rule, SharingRuleMatchContext context)
    {
        // A rule that names nothing to match against never matches by
        // accident — that would make it silently behave like a second global
        // policy, with no way to tell which one actually applied.
        var statesACriterion = false;

        if (!string.IsNullOrWhiteSpace(rule.CategoryPattern))
        {
            statesACriterion = true;
            if (!MatchesPattern(rule.CategoryPattern, context.Category))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(rule.TrackerPattern))
        {
            statesACriterion = true;
            if (!MatchesPattern(rule.TrackerPattern, context.TrackerHost))
            {
                return false;
            }
        }

        var requiredTags = SplitCsv(rule.TagsCsv);
        if (requiredTags.Count > 0)
        {
            statesACriterion = true;
            var held = (context.Tags ?? [])
                .Select(tag => tag.Trim())
                .Where(tag => tag.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tagsMatch = string.Equals(rule.TagMatchMode, SharingRuleItem.TagMatchAll, StringComparison.OrdinalIgnoreCase)
                ? requiredTags.All(held.Contains)
                : requiredTags.Any(held.Contains);
            if (!tagsMatch)
            {
                return false;
            }
        }

        return statesACriterion;
    }

    /// <summary>
    /// <c>*.example.com</c> matches that domain and any subdomain of it, the
    /// same wildcard shape Cleanuparr's own tracker-domain patterns use.
    /// Anything else is a plain, case-insensitive substring match, so a
    /// category pattern of "anime" matches a category of "tv-anime".
    /// </summary>
    private static bool MatchesPattern(string pattern, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmedPattern = pattern.Trim();
        if (trimmedPattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var domain = trimmedPattern[2..];
            return value.Equals(domain, StringComparison.OrdinalIgnoreCase)
                || value.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        }

        return value.Contains(trimmedPattern, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> SplitCsv(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
