namespace Deluno.Contracts;

/// <summary>
/// Radarr/Sonarr's own tag-routing rule (#808, extended to indexers and
/// notification connections by #990): a connection carrying tags is used
/// only for a title sharing one of them, and an untagged connection is used
/// for any title regardless of its tags. Shared by download clients,
/// indexers and notification connections — the eligibility rule is the same
/// for all three, and lives here (rather than in Deluno.Integrations, which
/// Deluno.Notifications cannot reference without a project cycle) so every
/// connection kind can reuse one implementation.
/// </summary>
public static class TagRoutingMatch
{
    public static bool IsEligible(string? connectionTags, IReadOnlyList<string>? titleTags)
    {
        var required = Split(connectionTags);
        if (required.Count == 0)
        {
            return true;
        }

        if (titleTags is null || titleTags.Count == 0)
        {
            return false;
        }

        var held = titleTags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return required.Any(held.Contains);
    }

    /// <summary>
    /// Why an action went to this connection, in words — null for an
    /// untagged connection, which takes any title and so is never the reason
    /// a routing choice needs explaining.
    /// </summary>
    public static string? Explain(string? connectionTags, IReadOnlyList<string>? titleTags, string connectionName)
    {
        var required = Split(connectionTags);
        if (required.Count == 0)
        {
            return null;
        }

        var held = titleTags?.Select(tag => tag.Trim()).Where(tag => tag.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedTag = required.FirstOrDefault(held.Contains);
        return matchedTag is null
            ? null
            : $"{connectionName} only takes titles tagged \"{matchedTag}\", and this one is.";
    }

    private static IReadOnlyList<string> Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
