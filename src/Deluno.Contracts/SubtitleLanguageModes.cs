namespace Deluno.Contracts;

/// <summary>
/// How many of a library's wanted languages a file needs.
///
/// Bazarr expresses this as an ordered list plus a cutoff <em>position</em>,
/// which folds two different intentions into one number. They are two words
/// here, and they are exactly what the bar under a poster counts.
/// </summary>
public static class SubtitleLanguageModes
{
    /// <summary>Every language listed. "English and Japanese."</summary>
    public const string All = "all";

    /// <summary>
    /// The first one obtainable, in order. "English, or Spanish if English
    /// cannot be found; do not fetch both."
    /// </summary>
    public const string First = "first";

    /// <summary>
    /// Anything unrecognised reads as <see cref="All"/>.
    ///
    /// The asymmetry is deliberate, and it is the same reasoning that made
    /// <see cref="WantedStatuses"/> refuse to guess: reading a stray value as
    /// <see cref="First"/> would quietly stop fetching languages somebody had
    /// asked for, and nothing on screen would say why.
    /// </summary>
    public static string Normalize(string? value)
        => string.Equals(value?.Trim(), First, StringComparison.OrdinalIgnoreCase) ? First : All;

    /// <summary>
    /// How many languages one file is expected to hold under this mode — which
    /// is the <c>wanted</c> half of the bar, per file.
    /// </summary>
    public static int WantedPerFile(IReadOnlyList<string> languages, string? mode)
        => languages.Count == 0 ? 0 : Normalize(mode) == First ? 1 : languages.Count;
}
