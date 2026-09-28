using Deluno.Libraries.Contracts;

namespace Deluno.Libraries.Routing;

/// <summary>
/// The title facts a routing rule can be matched against (#1033): what a
/// manual add, an import list or a Seerr approval already knows about a
/// title before it lands in any library.
/// </summary>
/// <param name="RequestedIn4k">
/// Whether this particular acquisition was asked for in 4K - Seerr says so
/// on the request itself; a manual add or list has no such notion and
/// passes false. This is what a "requested4k" rule condition matches
/// (#985's old per-connection 4K mapping, now an ordinary rule).
/// </param>
public sealed record LibraryRoutingMetadata(
    IReadOnlyList<string> Genres,
    string? OriginalLanguage,
    string? Certification,
    string? Studio,
    bool RequestedIn4k = false)
{
    /// <summary>
    /// TMDb's own convention (also what Sonarr/Radarr-adjacent tools use):
    /// animation whose original language is Japanese. Deluno has no
    /// dedicated "is this anime" field on a title yet, so an "anime" rule
    /// matches on this heuristic rather than nothing at all.
    /// </summary>
    public bool IsAnime =>
        Genres.Any(genre => string.Equals(genre, "Animation", StringComparison.OrdinalIgnoreCase)) &&
        string.Equals(OriginalLanguage, "ja", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What routing decided (#1033): the library a rule (or the caller's fallback) placed the title in, or nothing when neither applies.</summary>
public sealed record LibraryRoutingResult(
    LibraryItem? Library,
    DestinationRuleItem? Rule)
{
    /// <summary>No rule matched and no fallback library resolved either - Deluno refuses rather than guessing.</summary>
    public bool Matched => Library is not null;

    public static readonly LibraryRoutingResult NoMatch = new(null, null);
}

/// <summary>
/// Picks the library a title belongs in from Deluno's own destination rules
/// (#1033) - the same rules a manual add's folder placement already reads -
/// evaluated here against a title's metadata instead of a release's file
/// name. Shared by every caller that used to guess "the first library of
/// this type" or read a single fixed mapping: today that is Seerr, but nothing
/// here is Seerr-specific.
/// </summary>
public interface ILibraryRoutingResolver
{
    /// <param name="mediaType">"movies" or "tv" (also accepts "movie"/"series"/"shows").</param>
    /// <param name="fallbackLibraryId">
    /// Used only when no enabled routing rule matches. Null (or a library
    /// that no longer exists) with no rule match means no library resolves
    /// at all - the caller must refuse rather than guess.
    /// </param>
    LibraryRoutingResult Resolve(
        string? mediaType,
        LibraryRoutingMetadata metadata,
        IReadOnlyList<LibraryItem> libraries,
        IReadOnlyList<DestinationRuleItem> rules,
        string? fallbackLibraryId);
}

public sealed class LibraryRoutingResolver : ILibraryRoutingResolver
{
    public LibraryRoutingResult Resolve(
        string? mediaType,
        LibraryRoutingMetadata metadata,
        IReadOnlyList<LibraryItem> libraries,
        IReadOnlyList<DestinationRuleItem> rules,
        string? fallbackLibraryId)
    {
        var normalizedType = NormalizeMediaType(mediaType);
        var candidates = libraries
            .Where(library => NormalizeMediaType(library.MediaType) == normalizedType)
            .ToArray();
        if (candidates.Length == 0)
        {
            return LibraryRoutingResult.NoMatch;
        }

        var isAnime = metadata.IsAnime;
        var matchedRule = rules
            .Where(rule => rule.IsEnabled
                && !string.IsNullOrWhiteSpace(rule.LibraryId)
                && NormalizeMediaType(rule.MediaType) == normalizedType)
            .OrderBy(rule => rule.Priority)
            .ThenBy(rule => rule.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(rule => Matches(rule, metadata, isAnime));

        if (matchedRule is not null)
        {
            var ruleLibrary = candidates.FirstOrDefault(
                library => string.Equals(library.Id, matchedRule.LibraryId, StringComparison.OrdinalIgnoreCase));
            if (ruleLibrary is not null)
            {
                return new LibraryRoutingResult(ruleLibrary, matchedRule);
            }

            // The rule points at a library that no longer exists, or was
            // retyped to a different media type since - fall through to the
            // fallback rather than silently dropping the request.
        }

        if (!string.IsNullOrWhiteSpace(fallbackLibraryId))
        {
            var fallback = candidates.FirstOrDefault(
                library => string.Equals(library.Id, fallbackLibraryId, StringComparison.OrdinalIgnoreCase));
            if (fallback is not null)
            {
                return new LibraryRoutingResult(fallback, null);
            }
        }

        return LibraryRoutingResult.NoMatch;
    }

    private static bool Matches(DestinationRuleItem rule, LibraryRoutingMetadata metadata, bool isAnime)
    {
        var expected = rule.MatchValue.Trim();
        return rule.MatchKind.Trim().ToLowerInvariant() switch
        {
            "genre" => metadata.Genres.Any(genre => genre.Contains(expected, StringComparison.OrdinalIgnoreCase)),
            "language" => metadata.OriginalLanguage?.Contains(expected, StringComparison.OrdinalIgnoreCase) == true,
            "certification" => string.Equals(metadata.Certification, expected, StringComparison.OrdinalIgnoreCase),
            "anime" => isAnime == IsTrue(expected),
            "requested4k" => metadata.RequestedIn4k == IsTrue(expected),
            // "tag"/"quality"/"library" describe a release or an import-time
            // fact Deluno does not have yet when a title is only being
            // routed by its metadata, so they never match here.
            _ => false
        };
    }

    private static bool IsTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeMediaType(string? mediaType)
        => mediaType?.Trim().ToLowerInvariant() is "tv" or "series" or "shows" ? "tv" : "movies";
}
