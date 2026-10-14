namespace Deluno.Libraries.Contracts;

/// <summary>
/// The kinds of destination rule Deluno stores. A kind not in this list is refused,
/// never mapped to another kind (#1322).
/// </summary>
public static class DestinationRuleKinds
{
    public static readonly IReadOnlyList<string> All =
    [
        "genre", "tag", "studio", "title", "language", "quality", "anime", "certification", "library", "requested4k"
    ];

    /// <summary>The stored form of a kind, or null when the kind is not one Deluno knows.</summary>
    public static string? Normalize(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is not null && All.Contains(normalized) ? normalized : null;
    }
}
