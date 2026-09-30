namespace Deluno.Integrations.Metadata;

/// <summary>
/// Rules about which source a title may be linked to. An automatic refresh may
/// improve a link but never make it weaker; choosing a different source is a
/// person's decision (Fix match).
/// </summary>
public static class MetadataLinkPolicy
{
    /// <summary>
    /// True when <paramref name="match"/> would move a title that is linked to
    /// TMDb or TheTVDB onto OMDb, which carries far less and whose "tt" ids the
    /// TMDb lookup cannot read (#1091).
    /// </summary>
    public static bool WouldDowngradeLink(string? storedProvider, MetadataSearchResult match)
        => IsStrongProvider(storedProvider) &&
           string.Equals(match.Provider, "omdb", StringComparison.OrdinalIgnoreCase);

    private static bool IsStrongProvider(string? provider)
        => string.Equals(provider?.Trim(), "tmdb", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(provider?.Trim(), "tvdb", StringComparison.OrdinalIgnoreCase);
}
