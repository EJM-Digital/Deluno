using System.Text.RegularExpressions;
using Deluno.Platform.Contracts;

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
/// <summary>
/// What Deluno decided about the release a file came from, for the tokens that
/// describe the file rather than the title.
///
/// <para>These are read from the decision, never re-read from the incoming file
/// name. A name assembled by parsing the old name loses whatever the parser
/// cannot see - which is how a release Deluno chose <em>because</em> it was
/// Amazon ends up on disk with no sign of it.</para>
/// </summary>
public sealed record NamingDecision(
    string? Quality = null,
    string? ReleaseGroup = null,
    IReadOnlyList<string>? CustomFormats = null,
    IReadOnlyList<string>? Editions = null)
{
    public static NamingDecision None { get; } = new();

    public string CustomFormatText => Join(CustomFormats);

    public string EditionText => Join(Editions);

    private static string Join(IReadOnlyList<string>? values)
        => string.Join(" ", (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase));
}

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
        string? network = null,
        NamingDecision? decision = null,
        bool replaceIllegalCharacters = true,
        string colonReplacementMode = ColonReplacementModes.SmartReplace)
    {
        var resolved = ReplaceTokens(template, title, year, imdbId, tvDbId, qualityProfile, genre, tag, network, decision, replaceIllegalCharacters, colonReplacementMode);
        var segments = resolved
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(RemoveEmptyWrappers)
            .Select(segment => SanitizeSegment(segment, replaceIllegalCharacters, colonReplacementMode))
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();

        return segments.Length == 0
            ? SanitizeSegment(title, replaceIllegalCharacters, colonReplacementMode) is { Length: > 0 } fallback ? fallback : "Untitled"
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
        string? network = null,
        NamingDecision? decision = null,
        bool replaceIllegalCharacters = true,
        string colonReplacementMode = ColonReplacementModes.SmartReplace)
    {
        var resolved = ReplaceTokens(template, title, year, imdbId, tvDbId, qualityProfile, genre, tag, network, decision, replaceIllegalCharacters, colonReplacementMode);
        return SanitizeSegment(RemoveEmptyWrappers(resolved), replaceIllegalCharacters, colonReplacementMode);
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
        string? network,
        NamingDecision? decision,
        bool replaceIllegalCharacters = true,
        string colonReplacementMode = ColonReplacementModes.SmartReplace)
    {
        decision ??= NamingDecision.None;
        var source = string.IsNullOrWhiteSpace(template) ? "{Title} ({Year})" : template;
        return TokenPattern().Replace(source, match => match.Value.ToLowerInvariant() switch
        {
            "{quality}" => SanitizeSegment(decision.Quality, replaceIllegalCharacters, colonReplacementMode),
            "{release group}" => SanitizeSegment(decision.ReleaseGroup, replaceIllegalCharacters, colonReplacementMode),
            "{custom formats}" => SanitizeSegment(decision.CustomFormatText, replaceIllegalCharacters, colonReplacementMode),
            "{edition}" => SanitizeSegment(decision.EditionText, replaceIllegalCharacters, colonReplacementMode),
            "{movie title}" or "{series title}" or "{title}" => SanitizeSegment(title, replaceIllegalCharacters, colonReplacementMode),
            "{release year}" or "{series year}" or "{year}" => year?.ToString() ?? "Unknown Year",
            "{imdb id}" => SanitizeSegment(imdbId, replaceIllegalCharacters, colonReplacementMode),
            "{tvdb id}" => SanitizeSegment(tvDbId, replaceIllegalCharacters, colonReplacementMode),
            "{quality profile}" => SanitizeSegment(qualityProfile, replaceIllegalCharacters, colonReplacementMode),
            "{genre}" => SanitizeSegment(genre, replaceIllegalCharacters, colonReplacementMode),
            "{tag}" => SanitizeSegment(tag, replaceIllegalCharacters, colonReplacementMode),
            "{network}" => SanitizeSegment(network, replaceIllegalCharacters, colonReplacementMode),
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

    /// <summary>
    /// Turns whatever a title, token or template segment contains into
    /// something safe to put on disk. Shared with the import file-naming path
    /// through <see cref="FileNameSanitizer"/> so #757 and #759 cannot drift
    /// between the two.
    /// </summary>
    private static string SanitizeSegment(
        string? value,
        bool replaceIllegalCharacters = true,
        string colonReplacementMode = ColonReplacementModes.SmartReplace)
    {
        var cleaned = FileNameSanitizer.Sanitize(value, replaceIllegalCharacters, colonReplacementMode);

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
