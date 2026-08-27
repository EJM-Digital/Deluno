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
            _ => $"{alias}.created_utc"
        };

    /// <summary>
    /// Whether the sort value is a number rather than text, which decides how
    /// the token's value binds. SQLite compares by storage class, so binding a
    /// year as text would silently match nothing.
    /// </summary>
    public static bool IsNumeric(string sortField)
        => CatalogueSortFields.Normalize(sortField) is CatalogueSortFields.Year or CatalogueSortFields.Rating;

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
            CatalogueStatusFilters.Monitored => $"{alias}.monitored = 1",
            CatalogueStatusFilters.Unmonitored => $"{alias}.monitored = 0",
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
