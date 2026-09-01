using System.Text.RegularExpressions;

namespace Deluno.Platform;

/// <summary>
/// Renders the naming tokens exposed by the media-management UI.
///
/// <para>Folder formats are paths, not file names. Rendering the complete
/// pattern through a file-name sanitizer turns the separator in a pattern such
/// as <c>{Genre}\{Movie Title}</c> into a hyphen, and missing optional values
/// leave literal tokens behind. Keeping the rendering here gives the import,
/// destination preview, and rename-preview paths the same behaviour.</para>
/// </summary>
public static partial class NamingTemplateRenderer
{
    public static string RenderFolder(
        string? template,
        string? title,
        int? year,
        string? imdbId = null,
        string? tvDbId = null,
        string? qualityProfile = null,
        string? genre = null,
        string? tag = null,
        string? network = null)
    {
        var resolved = ReplaceTokens(template, title, year, imdbId, tvDbId, qualityProfile, genre, tag, network);
        var segments = resolved
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(RemoveEmptyWrappers)
            .Select(SanitizeSegment)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();

        return segments.Length == 0
            ? SanitizeSegment(title) is { Length: > 0 } fallback ? fallback : "Untitled"
            : Path.Combine(segments);
    }

    public static string RenderSegment(
        string? template,
        string? title,
        int? year,
        string? imdbId = null,
        string? tvDbId = null,
        string? qualityProfile = null,
        string? genre = null,
        string? tag = null,
        string? network = null)
    {
        var resolved = ReplaceTokens(template, title, year, imdbId, tvDbId, qualityProfile, genre, tag, network);
        return SanitizeSegment(RemoveEmptyWrappers(resolved));
    }

    private static string ReplaceTokens(
        string? template,
        string? title,
        int? year,
        string? imdbId,
        string? tvDbId,
        string? qualityProfile,
        string? genre,
        string? tag,
        string? network)
    {
        var source = string.IsNullOrWhiteSpace(template) ? "{Title} ({Year})" : template;
        return TokenPattern().Replace(source, match => match.Value.ToLowerInvariant() switch
        {
            "{movie title}" or "{series title}" or "{title}" => SanitizeSegment(title),
            "{release year}" or "{series year}" or "{year}" => year?.ToString() ?? "Unknown Year",
            "{imdb id}" => SanitizeSegment(imdbId),
            "{tvdb id}" => SanitizeSegment(tvDbId),
            "{quality profile}" => SanitizeSegment(qualityProfile),
            "{genre}" => SanitizeSegment(genre),
            "{tag}" => SanitizeSegment(tag),
            "{network}" => SanitizeSegment(network),
            // Keep a custom token visible. It is safer to show an unsupported
            // pattern than to silently turn a user's deliberate text into a
            // different name.
            _ => match.Value
        });
    }

    private static string RemoveEmptyWrappers(string value)
    {
        var cleaned = EmptyBracketPattern().Replace(value, string.Empty);
        cleaned = EmptyParenthesisPattern().Replace(cleaned, string.Empty);
        cleaned = EmptyTvDbBracketPattern().Replace(cleaned, string.Empty);
        return cleaned.Trim();
    }

    private static string SanitizeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value
            .Select(character => invalid.Contains(character) || character is '<' or '>' or ':' or '"' or '|' or '?' or '*'
                ? '-'
                : character)
            .ToArray())
            .Trim();

        // A user-entered template must not escape the selected library root.
        // Separators have already been split, so these are the only traversal
        // segments that can remain.
        return cleaned is "." or ".." ? string.Empty : cleaned;
    }

    [GeneratedRegex(@"\{[^{}]+\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"\[\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex EmptyBracketPattern();

    [GeneratedRegex(@"\(\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex EmptyParenthesisPattern();

    [GeneratedRegex(@"\[\s*tvdb[-_\s]*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmptyTvDbBracketPattern();
}
