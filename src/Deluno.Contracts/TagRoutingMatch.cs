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

    /// <summary>
    /// A library's own tag choices for one indexer (#1354): "Only for titles tagged" needs the title to hold one of
    /// them, "Never for titles tagged" refuses a title holding any of them, and a refusal wins. Both empty means every title.
    /// </summary>
    public static bool LibrarySourceIsEligible(string? onlyForTags, string? neverForTags, IReadOnlyList<string>? titleTags)
        => LibrarySourceRefusal(onlyForTags, neverForTags, titleTags, "the indexer", "this title") is null;

    /// <summary>Why a library's indexer is not asked for this title, in words; null when it is asked.</summary>
    public static string? LibrarySourceRefusal(string? onlyForTags, string? neverForTags, IReadOnlyList<string>? titleTags, string indexerName, string titleName)
    {
        var held = titleTags?.Select(tag => tag.Trim()).Where(tag => tag.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var never = Split(neverForTags).FirstOrDefault(held.Contains);
        if (never is not null)
        {
            return $"{indexerName} is never used for titles tagged \"{never}\", and {titleName} has that tag.";
        }

        var only = Split(onlyForTags);
        return only.Count > 0 && !only.Any(held.Contains)
            ? $"{indexerName} is only used for titles tagged {string.Join(" or ", only.Select(tag => $"\"{tag}\""))}, and {titleName} has none of them."
            : null;
    }

    private static IReadOnlyList<string> Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
