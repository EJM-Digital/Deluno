namespace Deluno.Contracts;

/// <summary>
/// The narrowing a catalogue page can be asked for beyond its status and its
/// library — quality, size, genre, year, runtime, rating.
///
/// <para><b>Named fields, not a rule engine.</b> There was a generic one here
/// before: <c>filterAndSortLibraryItems</c> with a 45-value <c>FilterField</c>
/// union, <c>matchesCustomRule</c> and <c>resolveRuleValue</c>, in the browser.
/// It was deleted in #302 and the note on its grave is worth repeating — it
/// could express filters nothing could answer. Its <c>downloading</c> and
/// <c>needsAttention</c> branches tested values nothing ever set, so both
/// matched zero rows forever and nothing said so.</para>
///
/// <para>Each field here is one real stored column with one meaning, applied in
/// SQL. There is no way to ask this for something it cannot answer, and adding a
/// filter means adding a column to read — which is the check that was missing.</para>
///
/// <para><b>Everything narrows.</b> Filters combine with AND, and each list
/// inside a filter is an OR: "Remux 2160p or WEB 2160p, and tagged Drama".
/// That is the only combination anybody actually asks a library for, and it is
/// the one a reader can predict from looking at the controls.</para>
/// </summary>
/// <param name="Qualities">
/// Quality tiers as the ladder names them — <c>WEB 2160p</c>, <c>Remux 1080p</c>.
/// Matched against the file a title actually has, so a missing title matches no
/// quality at all rather than matching on its target.
/// </param>
/// <param name="Genres">
/// Every genre listed must be present, because that is what a reader means by
/// picking two. Matched on whole genres — a title tagged "Melodrama" is not a
/// "Drama" match.
/// </param>
/// <param name="MinSizeGb">Size of the file on disk. A title with no file matches no size filter.</param>
/// <param name="MinRatingValue">The metadata score, where one is stored.</param>
public sealed record CatalogueFilters(
    IReadOnlyList<string>? Qualities = null,
    IReadOnlyList<string>? Genres = null,
    double? MinSizeGb = null,
    double? MaxSizeGb = null,
    int? MinYear = null,
    int? MaxYear = null,
    int? MinRuntimeMinutes = null,
    int? MaxRuntimeMinutes = null,
    double? MinRatingValue = null)
{
    public static readonly CatalogueFilters None = new();

    /// <summary>
    /// Whether anything is actually being asked for. A page with no filters must
    /// run exactly the query it ran before this existed — the same rule the
    /// subtitle rollup follows, and the reason a feature nobody uses costs
    /// nothing.
    /// </summary>
    public bool IsEmpty =>
        (Qualities is null || Qualities.Count == 0) &&
        (Genres is null || Genres.Count == 0) &&
        MinSizeGb is null && MaxSizeGb is null &&
        MinYear is null && MaxYear is null &&
        MinRuntimeMinutes is null && MaxRuntimeMinutes is null &&
        MinRatingValue is null;

    /// <summary>
    /// Reads the comma-separated form the query string carries, dropping blanks
    /// so a trailing comma is not a filter for the empty string.
    /// </summary>
    public static IReadOnlyList<string>? ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return parts.Length == 0 ? null : parts;
    }
}
