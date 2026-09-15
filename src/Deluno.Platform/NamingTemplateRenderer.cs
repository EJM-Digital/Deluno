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
        NamingDecision? decision = null)
    {
        var resolved = ReplaceTokens(template, title, year, imdbId, tvDbId, qualityProfile, genre, tag, network, decision);
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
        string? network = null,
        NamingDecision? decision = null)
    {
        var resolved = ReplaceTokens(template, title, year, imdbId, tvDbId, qualityProfile, genre, tag, network, decision);
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
        string? network,
        NamingDecision? decision)
    {
        decision ??= NamingDecision.None;
        var source = string.IsNullOrWhiteSpace(template) ? "{Title} ({Year})" : template;
        return TokenPattern().Replace(source, match => match.Value.ToLowerInvariant() switch
        {
            "{quality}" => SanitizeSegment(decision.Quality),
            "{release group}" => SanitizeSegment(decision.ReleaseGroup),
            "{custom formats}" => SanitizeSegment(decision.CustomFormatText),
            "{edition}" => SanitizeSegment(decision.EditionText),
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
        var cleaned = new string(value
            .Select(character => invalid.Contains(character) ||
                                 character is '/' or '\\' or '<' or '>' or ':' or '"' or '|' or '?' or '*'
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
