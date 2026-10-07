using System.Globalization;
using System.Text;

namespace Deluno.Integrations.Metadata;

/// <summary>
/// Puts the matches for a typed title in the order a person expects, and lists a
/// show once (#1163).
///
/// <para>A TV search asks two sources, TheTVDB and TMDb. The merged list used to
/// be TheTVDB's order first, then whatever only TMDb found: typing "Frieren"
/// listed Frieden, Friheden and two more loose TheTVDB matches above
/// "Frieren: Beyond Journey's End", the one title that actually contains the
/// word, and a show both sources knew could appear twice. So the order is decided
/// here, after the merge, from how well each title matches what was typed: the
/// exact or whole-word title first, then a title that starts with it, then the
/// year if one was typed, and only then how well rated it is.</para>
/// </summary>
public static class MetadataSearchRanking
{
    /// <summary>The score above which a title is a real match for the query rather than a loose one.</summary>
    public const int GoodMatch = 60;

    /// <summary>The lowest score that still counts as the title being typed: every word typed starts a word of the title.</summary>
    public const int StillTyping = 40;

    /// <summary>The matches in order, each show once. <paramref name="query"/> is the title as typed, with any typed year already taken off.</summary>
    public static IReadOnlyList<MetadataSearchResult> Rank(IReadOnlyList<MetadataSearchResult> results, string? query, int? year)
    {
        var unique = Deduplicate(results);
        if (unique.Count < 2 || string.IsNullOrWhiteSpace(query))
        {
            return unique;
        }

        return unique
            .Select((result, index) => (Result: result, Index: index, Score: Score(result, query, year)))
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Result.Rating ?? 0)
            .ThenBy(item => item.Index)
            .Select(item => item.Result)
            .ToArray();
    }

    /// <summary>How well this result matches what was typed. Higher is better; zero is no match at all.</summary>
    public static int Score(MetadataSearchResult result, string query, int? year)
    {
        var typed = Normalize(query);
        var score = typed.Length == 0 ? 0 : BestNameScore(result, typed);
        if (year is > 0 && result.Year is > 0)
        {
            score += result.Year == year ? 20 : -15;
        }

        return score;
    }

    /// <summary>The title's own words as a plain lowercase string: no accents, no punctuation, single spaces.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static int BestNameScore(MetadataSearchResult result, string typed)
    {
        var best = NameScore(Normalize(result.Title), typed);
        // The same show under its other names counts, a little less than its own title.
        foreach (var name in (result.AlternateTitles ?? []).Append(result.OriginalTitle))
        {
            var normalized = Normalize(name);
            if (normalized.Length > 0)
            {
                best = Math.Max(best, NameScore(normalized, typed) - 5);
            }
        }

        return Math.Max(best, 0);
    }

    private static int NameScore(string name, string typed)
    {
        if (name.Length == 0)
        {
            return 0;
        }

        if (name == typed)
        {
            return 100;
        }

        var paddedName = " " + name + " ";
        var paddedTyped = " " + typed + " ";
        if (name.StartsWith(typed + " ", StringComparison.Ordinal))
        {
            return 90;
        }

        if (paddedName.Contains(paddedTyped, StringComparison.Ordinal))
        {
            return 75;
        }

        var typedWords = typed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var nameWords = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (typedWords.All(word => nameWords.Contains(word, StringComparer.Ordinal)))
        {
            return 60;
        }

        // Still typing: "friere" is the start of a word in the name.
        if (typedWords.All(word => nameWords.Any(candidate => candidate.StartsWith(word, StringComparison.Ordinal))))
        {
            return 40;
        }

        return typedWords.Any(word => nameWords.Contains(word, StringComparer.Ordinal)) ? 15 : 0;
    }

    /// <summary>
    /// Each show once: two results are the same show when they share a TheTVDB id, or have the
    /// same title and year. The copy kept is the one with the most to show for it - TheTVDB's
    /// record, then one with artwork, then the better rated - in the place the first copy held.
    /// </summary>
    public static IReadOnlyList<MetadataSearchResult> Deduplicate(IReadOnlyList<MetadataSearchResult> results)
    {
        var kept = new List<MetadataSearchResult>(results.Count);
        foreach (var result in results)
        {
            var index = kept.FindIndex(existing => SameShow(existing, result));
            if (index < 0)
            {
                kept.Add(result);
            }
            else if (Richer(result, kept[index]))
            {
                kept[index] = result;
            }
        }

        return kept;
    }

    private static bool SameShow(MetadataSearchResult left, MetadataSearchResult right)
    {
        if (!string.IsNullOrWhiteSpace(left.TvDbId) && string.Equals(left.TvDbId, right.TvDbId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(left.ProviderId, right.ProviderId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var title = Normalize(left.Title);
        return title.Length > 0
            && title == Normalize(right.Title)
            && left.Year == right.Year;
    }

    private static bool Richer(MetadataSearchResult candidate, MetadataSearchResult current)
    {
        var candidateTvdb = !string.IsNullOrWhiteSpace(candidate.TvDbId);
        var currentTvdb = !string.IsNullOrWhiteSpace(current.TvDbId);
        if (candidateTvdb != currentTvdb)
        {
            return candidateTvdb;
        }

        var candidateArt = !string.IsNullOrWhiteSpace(candidate.PosterUrl);
        var currentArt = !string.IsNullOrWhiteSpace(current.PosterUrl);
        if (candidateArt != currentArt)
        {
            return candidateArt;
        }

        return (candidate.Rating ?? 0) > (current.Rating ?? 0);
    }
}
