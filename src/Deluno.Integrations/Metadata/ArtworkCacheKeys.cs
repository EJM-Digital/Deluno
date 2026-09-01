namespace Deluno.Integrations.Metadata;

/// <summary>
/// Parses the deliberately narrow URL shape used for artwork localized by
/// Deluno. Remote provider URLs are not cache references and must never be
/// treated as one during maintenance.
/// </summary>
public static class ArtworkCacheKeys
{
    private const string LocalArtworkPrefix = "/api/metadata/artwork/";

    public static bool TryGet(string? url, out string cacheKey)
    {
        cacheKey = string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var trimmed = url.Trim();
        if (!trimmed.StartsWith(LocalArtworkPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidate = trimmed[LocalArtworkPrefix.Length..].TrimEnd('/');
        if (candidate.Length != 64 || candidate.Any(character => !IsHex(character)))
        {
            return false;
        }

        cacheKey = candidate.ToLowerInvariant();
        return true;
    }

    private static bool IsHex(char character)
        => character is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';
}
