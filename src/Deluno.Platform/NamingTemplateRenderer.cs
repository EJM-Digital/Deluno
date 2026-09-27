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
        bool replaceIllegalCharacters = true)
    {
        var resolved = ReplaceTokens(template, title, year, imdbId, tvDbId, qualityProfile, genre, tag, network, decision, replaceIllegalCharacters);
        var segments = resolved
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(RemoveEmptyWrappers)
            .Select(segment => SanitizeSegment(segment, replaceIllegalCharacters))
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();

        return segments.Length == 0
            ? SanitizeSegment(title, replaceIllegalCharacters) is { Length: > 0 } fallback ? fallback : "Untitled"
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
        bool replaceIllegalCharacters = true)
    {
        var resolved = ReplaceTokens(template, title, year, imdbId, tvDbId, qualityProfile, genre, tag, network, decision, replaceIllegalCharacters);
        return SanitizeSegment(RemoveEmptyWrappers(resolved), replaceIllegalCharacters);
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
        bool replaceIllegalCharacters = true)
    {
        decision ??= NamingDecision.None;
        var source = string.IsNullOrWhiteSpace(template) ? "{Title} ({Year})" : template;
        return TokenPattern().Replace(source, match => match.Value.ToLowerInvariant() switch
        {
            "{quality}" => SanitizeSegment(decision.Quality, replaceIllegalCharacters),
            "{release group}" => SanitizeSegment(decision.ReleaseGroup, replaceIllegalCharacters),
            "{custom formats}" => SanitizeSegment(decision.CustomFormatText, replaceIllegalCharacters),
            "{edition}" => SanitizeSegment(decision.EditionText, replaceIllegalCharacters),
            "{movie title}" or "{series title}" or "{title}" => SanitizeSegment(title, replaceIllegalCharacters),
            "{release year}" or "{series year}" or "{year}" => year?.ToString() ?? "Unknown Year",
            "{imdb id}" => SanitizeSegment(imdbId, replaceIllegalCharacters),
            "{tvdb id}" => SanitizeSegment(tvDbId, replaceIllegalCharacters),
            "{quality profile}" => SanitizeSegment(qualityProfile, replaceIllegalCharacters),
            "{genre}" => SanitizeSegment(genre, replaceIllegalCharacters),
            "{tag}" => SanitizeSegment(tag, replaceIllegalCharacters),
            "{network}" => SanitizeSegment(network, replaceIllegalCharacters),
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
    /// something safe to put on disk.
    ///
    /// <para>Radarr and Sonarr default to replacing an illegal character with
    /// a dash; unchecking their "Replace Illegal Characters" switch removes it
    /// instead (#757). <paramref name="replaceIllegalCharacters"/> is that
    /// switch — false removes every illegal character, colon included,
    /// regardless of any colon mode.</para>
    /// </summary>
    private static string SanitizeSegment(string? value, bool replaceIllegalCharacters = true)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // Both separators, always, whoever is running.
        //
        // Path.GetInvalidFileNameChars() answers for the host: on Windows it
        // returns the whole set, on Linux it returns exactly NUL and '/'. The
        // characters after it were already spelled out to cover that gap - and
        // the backslash was not among them. So on Linux a title containing one
        // survived, and RenderFolder then split on it: "Title\with separators"
        // became "Title/with separators", a nested folder built out of a title,
        // which is the one thing this function exists to prevent. The container
        // image is the Linux one, and a library it writes is read by Windows
        // clients over a share.
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            var isIllegal = invalid.Contains(character) ||
                character is '/' or '\\' or '<' or '>' or ':' or '"' or '|' or '?' or '*';
            if (!isIllegal)
            {
                builder.Append(character);
            }
            else if (replaceIllegalCharacters)
            {
                builder.Append('-');
            }
        }

        var cleaned = builder.ToString().Trim();

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
