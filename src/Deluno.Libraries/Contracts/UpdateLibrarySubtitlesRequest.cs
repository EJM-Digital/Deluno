namespace Deluno.Libraries.Contracts;

/// <summary>
/// The subtitle languages this library wants.
/// </summary>
/// <param name="Languages">
/// Ordered ISO 639-1 codes, most wanted first — <c>["en", "ja"]</c>. Empty means
/// no subtitles are wanted, and a title with none wanted draws no bar.
/// </param>
/// <param name="Mode">
/// <c>all</c> or <c>first</c>. See <see cref="LibraryItem.SubtitleLanguageMode"/>.
/// </param>
public sealed record UpdateLibrarySubtitlesRequest(
    IReadOnlyList<string>? Languages,
    string? Mode,
    /// <summary>
    /// What a subtitle with no language in its name is. Null or blank means
    /// "do not guess", which is the default and what Deluno has always done.
    /// </summary>
    string? UnknownLanguage = null,
    /// <summary>Whether a track inside the container counts as held.</summary>
    bool EmbeddedCounts = true);
