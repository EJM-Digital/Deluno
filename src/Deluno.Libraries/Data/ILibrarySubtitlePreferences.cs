using Deluno.Contracts;

namespace Deluno.Libraries.Data;

/// <summary>
/// What a library asks for in the way of subtitles, and nothing else about it.
///
/// Deliberately narrow. A catalogue page needs two facts per library — the
/// languages and the mode — and reading a full library record with its quality
/// profile, its routing and its automation state to get them would put all of
/// that on the hot path for every page of every shelf.
///
/// It is also the gate that keeps this feature free for anybody not using it:
/// a library with no languages returns <see cref="LibrarySubtitlePreference"/>
/// with none, the page skips the subtitle rollup entirely, and the catalogue
/// costs exactly what it cost before Subber existed.
/// </summary>
public interface ILibrarySubtitlePreferences
{
    Task<IReadOnlyDictionary<string, LibrarySubtitlePreference>> GetSubtitlePreferencesAsync(
        CancellationToken cancellationToken);
}

public sealed record LibrarySubtitlePreference(
    string LibraryId,
    IReadOnlyList<string> Languages,
    string Mode,
    /// <summary>
    /// What a subtitle with no language in its name is taken to be. Empty means
    /// "do not guess", which is the default — see <c>LibraryItem</c>.
    /// </summary>
    string UnknownLanguage = "",
    /// <summary>Whether a track inside the container counts as held.</summary>
    bool EmbeddedCounts = true)
{
    /// <summary>
    /// The <c>wanted</c> half of the bar under a poster, per file. One rule,
    /// in <see cref="SubtitleLanguageModes"/>, read by everything that draws or
    /// counts it.
    /// </summary>
    public int WantedPerFile => SubtitleLanguageModes.WantedPerFile(Languages, Mode);
}
