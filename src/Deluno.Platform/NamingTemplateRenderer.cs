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
    IReadOnlyList<string>? Editions = null,
    // #760: the title in its original language, from metadata -- not
    // re-derived from the release name, which usually only carries the
    // English/library title.
    string? OriginalTitle = null,
    // #760: the incoming file's own name (no extension), for a token that
    // keeps a trace of what a release was actually called.
    string? OriginalFileName = null,
    // #760: a short, stable identifier for the release this decision came
    // from -- not read back from the destination name, so a template that
    // includes it still tells two same-titled releases apart.
    string? ReleaseHash = null,
    // #760: MediaInfo tokens. Parsed from the release/file name, the same
    // source MediaFileNameFacts already reads Quality's codec/audio evidence
    // from -- null wherever the name says nothing about it.
    string? VideoCodec = null,
    string? AudioCodec = null,
    string? AudioChannels = null,
    string? VideoDynamicRangeType = null,
    string? VideoBitDepth = null,
    // #760/#761: an episode's own dated or absolute number, for daily and
    // anime naming.
    DateOnly? AirDate = null,
    int? AbsoluteEpisodeNumber = null)
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
        string[] Build(int? titleMaxBytes)
        {
            var resolved = ReplaceTokens(template, title, year, imdbId, tvDbId, qualityProfile, genre, tag, network, decision, replaceIllegalCharacters, colonReplacementMode, titleMaxBytes);
            return resolved
                .Split(['/', '\\'],StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(RemoveEmptyWrappers)
                .Select(segment => SanitizeSegment(segment, replaceIllegalCharacters, colonReplacementMode))
                .Where(segment => !string.IsNullOrWhiteSpace(segment))
                .ToArray();
        }

        var segments = Build(null);
        var overflow = segments.Length == 0 ? 0 : segments.Max(FileNameSanitizer.ByteCount) - FileNameSanitizer.MaxNameBytes;
        if (overflow > 0)
        {
            // Shorten the title first, so the year and everything after it stay.
            var titleBytes = FileNameSanitizer.ByteCount(SanitizeSegment(title, replaceIllegalCharacters, colonReplacementMode));
            segments = Build(Math.Max(0, titleBytes - overflow));
        }

        segments = segments
            .Select(segment => FitSegment(segment, FileNameSanitizer.MaxNameBytes))
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();

        return segments.Length == 0
            ? SanitizeSegment(title, replaceIllegalCharacters, colonReplacementMode) is { Length: > 0 } fallback
                ? FitSegment(fallback, FileNameSanitizer.MaxNameBytes)
                : "Untitled"
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
        string colonReplacementMode = ColonReplacementModes.SmartReplace,
        int reserveBytes = 0)
    {
        // reserveBytes: what the caller appends after the segment (a file extension),
        // so the whole name stays within the limit and the extension is kept.
        var limit = FileNameSanitizer.MaxNameBytes - Math.Max(0, reserveBytes);
        string Build(int? titleMaxBytes)
            => SanitizeSegment(
                RemoveEmptyWrappers(ReplaceTokens(template, title, year, imdbId, tvDbId, qualityProfile, genre, tag, network, decision, replaceIllegalCharacters, colonReplacementMode, titleMaxBytes)),
                replaceIllegalCharacters,
                colonReplacementMode);

        var rendered = Build(null);
        var overflow = FileNameSanitizer.ByteCount(rendered) - limit;
        if (overflow > 0)
        {
            // Shorten the title first, so the year and everything after it stay.
            var titleBytes = FileNameSanitizer.ByteCount(SanitizeSegment(title, replaceIllegalCharacters, colonReplacementMode));
            rendered = Build(Math.Max(0, titleBytes - overflow));
        }

        return FitSegment(rendered, limit);
    }

    /// <summary>
    /// The last resort when no title was left to shorten: cut the name, keeping
    /// a trailing year, and never split a character.
    /// </summary>
    private static string FitSegment(string segment, int maxBytes)
    {
        if (FileNameSanitizer.ByteCount(segment) <= maxBytes)
        {
            return segment;
        }

        var tail = TrailingYearPattern().Match(segment);
        var tailText = tail.Success && FileNameSanitizer.ByteCount(tail.Value) < maxBytes ? tail.Value : string.Empty;
        var head = segment[..(segment.Length - tailText.Length)];
        var cut = FileNameSanitizer.LimitBytes(head, maxBytes - FileNameSanitizer.ByteCount(tailText));
        return FileNameSanitizer.TrimEnd((cut + tailText).Trim());
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
        string colonReplacementMode = ColonReplacementModes.SmartReplace,
        int? titleMaxBytes = null)
    {
        decision ??= NamingDecision.None;
        var source = string.IsNullOrWhiteSpace(template) ? "{Title} ({Year})" : template;
        return TokenPattern().Replace(source, match => match.Value.ToLowerInvariant() switch
        {
            "{quality}" => SanitizeSegment(decision.Quality, replaceIllegalCharacters, colonReplacementMode),
            "{release group}" => SanitizeSegment(decision.ReleaseGroup, replaceIllegalCharacters, colonReplacementMode),
            "{custom formats}" => SanitizeSegment(decision.CustomFormatText, replaceIllegalCharacters, colonReplacementMode),
            "{edition}" => SanitizeSegment(decision.EditionText, replaceIllegalCharacters, colonReplacementMode),
            "{movie title}" or "{series title}" or "{title}" => titleMaxBytes is { } max
                ? FileNameSanitizer.LimitBytes(SanitizeSegment(title, replaceIllegalCharacters, colonReplacementMode), max)
                : SanitizeSegment(title, replaceIllegalCharacters, colonReplacementMode),
            "{release year}" or "{series year}" or "{year}" => year?.ToString() ?? "Unknown Year",
            "{imdb id}" => SanitizeSegment(imdbId, replaceIllegalCharacters, colonReplacementMode),
            "{tvdb id}" => SanitizeSegment(tvDbId, replaceIllegalCharacters, colonReplacementMode),
            "{quality profile}" => SanitizeSegment(qualityProfile, replaceIllegalCharacters, colonReplacementMode),
            "{genre}" => SanitizeSegment(genre, replaceIllegalCharacters, colonReplacementMode),
            "{tag}" => SanitizeSegment(tag, replaceIllegalCharacters, colonReplacementMode),
            "{network}" => SanitizeSegment(network, replaceIllegalCharacters, colonReplacementMode),
            "{original title}" => SanitizeSegment(decision.OriginalTitle, replaceIllegalCharacters, colonReplacementMode),
            "{original filename}" => SanitizeSegment(decision.OriginalFileName, replaceIllegalCharacters, colonReplacementMode),
            "{release hash}" => decision.ReleaseHash ?? string.Empty,
            "{mediainfo videocodec}" => decision.VideoCodec ?? string.Empty,
            "{mediainfo audiocodec}" => decision.AudioCodec ?? string.Empty,
            "{mediainfo audiochannels}" => decision.AudioChannels ?? string.Empty,
            "{mediainfo videodynamicrangetype}" => decision.VideoDynamicRangeType ?? string.Empty,
            "{mediainfo videobitdepth}" => decision.VideoBitDepth ?? string.Empty,
            "{air-date}" or "{air date}" => decision.AirDate?.ToString("yyyy-MM-dd") ?? string.Empty,
            "{absolute:000}" => decision.AbsoluteEpisodeNumber?.ToString("D3") ?? string.Empty,
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
        return cleaned is "." or ".." ? string.Empty : FileNameSanitizer.TrimEnd(cleaned);
    }

    [GeneratedRegex(@"\s*[\(\[]\s*(?:19|20|21)\d{2}\s*[\)\]]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingYearPattern();

    [GeneratedRegex(@"\{[^{}]+\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"\[\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex EmptyBracketPattern();

    [GeneratedRegex(@"\(\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex EmptyParenthesisPattern();

    [GeneratedRegex(@"\[\s*tvdb[-_\s]*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmptyTvDbBracketPattern();
}
