using System.Data.Common;
using System.Globalization;
using Deluno.Contracts;

namespace Deluno.Infrastructure.Storage;

/// <summary>
/// The keyset (seek) mechanics shared by the movie and series catalogue pages.
///
/// Both catalogues page the same way and differ only in table and column names,
/// and this is the part that is easy to get subtly wrong: the comparison in the
/// WHERE clause has to be the exact expression in the ORDER BY, including the
/// NULL handling and the tiebreaker, or a page will skip or repeat rows at the
/// boundary. Writing it once means it can only be wrong in one place.
/// </summary>
public static class CatalogueKeyset
{
    /// <summary>
    /// The SQL expression a sort field orders by.
    ///
    /// Every expression is total — no NULLs — so ordering is deterministic and
    /// the seek comparison never has to reason about three-valued logic.
    /// <c>lower(title)</c> rather than a collation because that is the form the
    /// existing title index is built on.
    /// </summary>
    public static string SortExpression(string sortField, string alias, string yearColumn)
        => CatalogueSortFields.Normalize(sortField) switch
        {
            CatalogueSortFields.Title => $"lower({alias}.title)",
            CatalogueSortFields.Year => $"COALESCE({alias}.{yearColumn}, -1)",
            CatalogueSortFields.Rating => $"COALESCE({alias}.rating, -1)",
            // Both of these have had an index since V0011/V0012 and neither has
            // ever been offered as a sort — the same shape as the codec and
            // release-group columns the list displayed for months without
            // anything populating them.
            CatalogueSortFields.Runtime => $"COALESCE({alias}.runtime_minutes, -1)",
            CatalogueSortFields.Popularity => $"COALESCE({alias}.popularity, -1)",
            _ => $"{alias}.created_utc"
        };

    /// <summary>
    /// Whether the sort value is a number rather than text, which decides how
    /// the token's value binds. SQLite compares by storage class, so binding a
    /// year as text would silently match nothing.
    /// </summary>
    public static bool IsNumeric(string sortField)
        => CatalogueSortFields.Normalize(sortField)
            is CatalogueSortFields.Year
            or CatalogueSortFields.Rating
            or CatalogueSortFields.Runtime
            or CatalogueSortFields.Popularity;

    /// <summary>
    /// <c>ORDER BY</c> for a page. Rows sharing a sort value are broken by id,
    /// so the order is total.
    ///
    /// The tiebreaker follows the sort direction rather than being fixed
    /// ascending, and that is not cosmetic. An index is single-direction: asking
    /// for <c>value DESC, id ASC</c> makes SQLite walk the index backwards and
    /// then re-sort each tie group, which it reports as
    /// <c>USE TEMP B-TREE FOR RIGHT PART OF ORDER BY</c>. Harmless when values
    /// are distinct — and 35ms a page on a freshly imported library, where every
    /// rating is still null and the whole catalogue is one tie group. Matching
    /// the directions lets the same index serve both ways round.
    /// </summary>
    public static string OrderBy(string sortExpression, string alias, bool descending)
    {
        var direction = descending ? "DESC" : "ASC";
        return $"{sortExpression} {direction}, {alias}.id {direction}";
    }

    /// <summary>
    /// The "everything after the last row I saw" predicate, or an empty string
    /// for the first page. The id comparison flips with the direction to match
    /// <see cref="OrderBy"/>; if the two disagreed, a page boundary inside a tie
    /// group would skip or repeat rows.
    /// </summary>
    public static string SeekPredicate(string sortExpression, string alias, bool descending)
    {
        var comparison = descending ? "<" : ">";
        return $"({sortExpression} {comparison} @seekValue "
               + $"OR ({sortExpression} = @seekValue AND {alias}.id {comparison} @seekId))";
    }

    /// <summary>
    /// Binds a decoded token. The value is carried as text and converted here,
    /// so the token stays a single opaque string whatever the sort field is.
    /// </summary>
    public static void BindSeek(DbCommand command, CataloguePageToken token, string sortField)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@seekValue";

        if (IsNumeric(sortField))
        {
            parameter.Value = double.TryParse(token.SortValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number
                : -1d;
        }
        else
        {
            parameter.Value = token.SortValue ?? string.Empty;
        }

        command.Parameters.Add(parameter);

        var idParameter = command.CreateParameter();
        idParameter.ParameterName = "@seekId";
        idParameter.Value = token.Id;
        command.Parameters.Add(idParameter);
    }

    /// <summary>
    /// Free-text search over title and genres.
    ///
    /// A leading wildcard cannot use an index, so this scans the entries table.
    /// That is honest for a library-sized catalogue and fast enough there; if it
    /// ever needs to be sub-linear the answer is FTS5, not a smaller result.
    /// </summary>
    public static string SearchFilter(string alias)
        => $"(lower({alias}.title) LIKE @search OR lower(COALESCE({alias}.genres, '')) LIKE @search)";

    public static string StatusFilter(
        string status,
        string alias,
        string hasFileExpression,
        string? upgradeExpression = null,
        string? coveredExpression = null,
        string? upcomingExpression = null)
        => CatalogueStatusFilters.Normalize(status) switch
        {
            CatalogueStatusFilters.Downloaded => hasFileExpression,
            // Missing is a *state*, not the absence of a file: a title that is
            // not out yet has no file either, and calling it missing counted it
            // against the library from the day it was added.
            CatalogueStatusFilters.Missing when !string.IsNullOrWhiteSpace(upcomingExpression)
                => $"NOT {hasFileExpression} AND NOT {upcomingExpression}",
            CatalogueStatusFilters.Missing => $"NOT {hasFileExpression}",
            CatalogueStatusFilters.Upgrades when !string.IsNullOrWhiteSpace(upgradeExpression) => upgradeExpression,
            CatalogueStatusFilters.Covered when !string.IsNullOrWhiteSpace(coveredExpression) => coveredExpression,
            CatalogueStatusFilters.Upcoming when !string.IsNullOrWhiteSpace(upcomingExpression) => upcomingExpression,
            _ => string.Empty
        };

    /// <summary>
    /// Whether Deluno acts on the title — a separate axis from
    /// <see cref="StatusFilter"/>, so the two can be asked together. `null` is
    /// "either", and produces no predicate at all.
    /// </summary>
    public static string MonitoredFilter(bool? monitored, string alias)
        => monitored switch
        {
            true => $"{alias}.monitored = 1",
            false => $"{alias}.monitored = 0",
            null => string.Empty
        };

    /// <summary>
    /// The custom narrowing — quality, size, genre, year, runtime, rating —
    /// as one predicate, written once for both catalogues.
    ///
    /// <para>These sit in the WHERE clause beside the search and status
    /// filters, so they narrow the rows an index walk produces rather than
    /// changing what drives it: the page is still a seek on the sort column and
    /// still stops as soon as it has enough rows. A filter nothing matches
    /// costs a walk, which is the honest price of asking a question with no
    /// answer.</para>
    ///
    /// <para>Quality and size read the joined wanted state, so a title with no
    /// file matches neither — which is what a reader means. Asking for "under
    /// 5 GB" and being shown ten titles that have no file at all would be the
    /// same class of answer as the badge that used to show a target quality as
    /// if it were owned.</para>
    ///
    /// <para>Nothing here interpolates a caller's value. Every one is a bound
    /// parameter; the only interpolation is the parameter *names*, generated
    /// from a count.</para>
    /// </summary>
    public static string CustomFilters(CatalogueFilters? filters, string alias, string yearColumn)
    {
        if (filters is null || filters.IsEmpty)
        {
            return string.Empty;
        }

        var predicates = new List<string>();

        if (filters.Qualities is { Count: > 0 } qualities)
        {
            var names = string.Join(", ", qualities.Select((_, index) => $"@quality{index}"));
            predicates.Add($"lower(COALESCE(ws.current_quality, '')) IN ({names})");
        }

        if (filters.Genres is { Count: > 0 } genres)
        {
            // Whole genres, not substrings: bracketing the list and each term in
            // commas is what stops "Drama" matching a title tagged "Melodrama".
            // Every genre asked for must be present, because that is what a
            // reader means by picking two.
            for (var index = 0; index < genres.Count; index++)
            {
                predicates.Add(
                    $"(',' || replace(lower(COALESCE({alias}.genres, '')), ', ', ',') || ',') LIKE @genre{index}");
            }
        }

        if (filters.MinSizeGb is not null)
        {
            predicates.Add("ws.file_size_bytes >= @minSizeBytes");
        }

        if (filters.MaxSizeGb is not null)
        {
            predicates.Add("ws.file_size_bytes <= @maxSizeBytes");
        }

        if (filters.MinYear is not null)
        {
            predicates.Add($"{alias}.{yearColumn} >= @minYear");
        }

        if (filters.MaxYear is not null)
        {
            predicates.Add($"{alias}.{yearColumn} <= @maxYear");
        }

        if (filters.MinRuntimeMinutes is not null)
        {
            predicates.Add($"{alias}.runtime_minutes >= @minRuntime");
        }

        if (filters.MaxRuntimeMinutes is not null)
        {
            predicates.Add($"{alias}.runtime_minutes <= @maxRuntime");
        }

        if (filters.MinRatingValue is not null)
        {
            predicates.Add($"{alias}.rating >= @minRating");
        }

        return predicates.Count == 0 ? string.Empty : string.Join(" AND ", predicates);
    }

    /// <summary>
    /// Binds what <see cref="CustomFilters"/> wrote. Kept immediately beside it
    /// for the same reason <c>PageColumns</c> and <c>Read</c> are: two lists
    /// that must agree, in one place, so they can only disagree visibly.
    /// </summary>
    public static void BindCustomFilters(DbCommand command, CatalogueFilters? filters)
    {
        if (filters is null || filters.IsEmpty)
        {
            return;
        }

        if (filters.Qualities is { Count: > 0 } qualities)
        {
            for (var index = 0; index < qualities.Count; index++)
            {
                Bind(command, $"@quality{index}", qualities[index].Trim().ToLowerInvariant());
            }
        }

        if (filters.Genres is { Count: > 0 } genres)
        {
            for (var index = 0; index < genres.Count; index++)
            {
                Bind(command, $"@genre{index}", $"%,{genres[index].Trim().ToLowerInvariant()},%");
            }
        }

        if (filters.MinSizeGb is { } minSize)
        {
            Bind(command, "@minSizeBytes", (long)(minSize * 1024 * 1024 * 1024));
        }

        if (filters.MaxSizeGb is { } maxSize)
        {
            Bind(command, "@maxSizeBytes", (long)(maxSize * 1024 * 1024 * 1024));
        }

        if (filters.MinYear is { } minYear) Bind(command, "@minYear", minYear);
        if (filters.MaxYear is { } maxYear) Bind(command, "@maxYear", maxYear);
        if (filters.MinRuntimeMinutes is { } minRuntime) Bind(command, "@minRuntime", minRuntime);
        if (filters.MaxRuntimeMinutes is { } maxRuntime) Bind(command, "@maxRuntime", maxRuntime);
        if (filters.MinRatingValue is { } minRating) Bind(command, "@minRating", minRating);
    }

    private static void Bind(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// An empty predicate means "everything", which is <c>1 = 1</c> — needed
    /// wherever a predicate has to sit inside a larger expression, such as the
    /// CASE arms the facet counts are built from.
    /// </summary>
    public static string Always(string predicate)
        => string.IsNullOrWhiteSpace(predicate) ? "1 = 1" : predicate;

    /// <summary>
    /// Joins the filters that apply, and falls back to <c>1 = 1</c> so the
    /// caller can always write <c>WHERE {filter}</c> without checking.
    /// </summary>
    public static string CombineFilters(params string[] filters)
    {
        var applied = filters.Where(filter => !string.IsNullOrEmpty(filter)).ToArray();
        return applied.Length == 0 ? "1 = 1" : string.Join(" AND ", applied);
    }

    public static void BindSearch(DbCommand command, string? search)
    {
        if (search is null)
        {
            return;
        }

        var parameter = command.CreateParameter();
        parameter.ParameterName = "@search";
        parameter.Value = "%" + search.ToLowerInvariant() + "%";
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// The token value for the last row of a page, read back from the same
    /// expression the page was ordered by.
    /// </summary>
    public static string ReadSortValue(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? string.Empty
            : reader.GetFieldType(ordinal) == typeof(string)
                ? reader.GetString(ordinal)
                : Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture);
}
