using System.Globalization;
using System.Text;

namespace Deluno.Contracts;

/// <summary>
/// What a title is filed under, as opposed to what it is called.
///
/// <para><b>The problem.</b> Deluno orders a shelf by <c>lower(title)</c>, so
/// <i>The Matrix</i> sits under <b>T</b>. Radarr and Sonarr both file it under
/// <b>M</b>. It never mattered much while the shelf was paged; the A–Z rail made
/// it plain, because on any real library <b>T</b> holds every title beginning
/// "The" and clicking <b>M</b> to find <i>The Matrix</i> finds nothing.</para>
///
/// <para><b>English articles only, and that is not laziness.</b> Radarr strips
/// <c>The</c>, <c>A</c> and <c>An</c> and stops there, and the reason is that
/// nothing at sort time knows a title's language. <i>Los Olvidados</i> should
/// file under <b>O</b> for a Spanish speaker — but strip <c>Los</c>
/// unconditionally and <i>Los Angeles Plays Itself</i>, an English title, files
/// under <b>A</b>. The same trap waits in <c>El</c>, <c>La</c>, <c>Der</c> and
/// <c>Die</c>: every one of them is also an ordinary word, or the start of a
/// proper noun, in some other language on the same shelf.</para>
///
/// <para>Deluno now stores <c>original_language</c> on every title, so a later
/// version could strip per language and be right about both. That is a real
/// feature and not this one; doing it blind would trade a visible problem for an
/// invisible one.</para>
///
/// <para><b>This is the rule, and it exists once.</b> SQLite computes it in a
/// trigger so no write path can forget it, and C# computes it wherever a title
/// is bucketed for the rail. Two languages, one rule — which is the shape every
/// defect in this codebase has had — so <see cref="SqlExpression"/> is
/// <i>generated from the same list</i> <see cref="For"/> reads, and a test runs
/// both over the same titles and fails if they ever disagree.</para>
/// </summary>
public static class SortTitle
{
    /// <summary>
    /// Longest first, so <c>an</c> is tried before <c>a</c> and
    /// <i>An Education</i> does not become <i>n Education</i>.
    /// </summary>
    public static readonly IReadOnlyList<string> Articles = ["the", "an", "a"];

    /// <summary>
    /// The title as it should be ordered: leading article removed, folded to
    /// lower case, and trimmed.
    ///
    /// <para>A title that is <i>only</i> an article keeps it. <i>The</i> is a
    /// real film, and filing it under nothing at all would put it in a bucket
    /// the rail cannot name.</para>
    /// </summary>
    public static string For(string? title)
    {
        var trimmed = (title ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        foreach (var article in Articles)
        {
            var prefix = article + " ";
            if (trimmed.Length > prefix.Length &&
                trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[prefix.Length..].Trim().ToLowerInvariant();
            }
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// The same rule as SQL, over a column or an expression.
    ///
    /// <para>Built from <see cref="Articles"/> rather than written out, so the
    /// list cannot be edited in one language and not the other. The
    /// <c>length</c> guard is what keeps a title that is only an article
    /// intact, and it matches the one in <see cref="For"/>.</para>
    /// </summary>
    public static string SqlExpression(string column)
    {
        var sql = new StringBuilder("CASE");

        foreach (var article in Articles)
        {
            var prefix = article.Length + 1;

            sql.Append(CultureInfo.InvariantCulture, $" WHEN length(trim({column})) > {prefix}")
               .Append(CultureInfo.InvariantCulture, $" AND lower(substr(trim({column}), 1, {prefix})) = '{article} '")
               .Append(CultureInfo.InvariantCulture, $" THEN lower(trim(substr(trim({column}), {prefix + 1})))");
        }

        return sql.Append(CultureInfo.InvariantCulture, $" ELSE lower(trim({column})) END").ToString();
    }
}
