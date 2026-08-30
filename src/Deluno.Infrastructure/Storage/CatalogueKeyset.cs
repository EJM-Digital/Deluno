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
    {
        var normalized = CatalogueSortFields.Normalize(sortField);

        // One order per rating source, from the same list the columns come
        // from. Null sorts last in both directions -- see the -1 below: a
        // library where most titles have no Metacritic score would otherwise
        // open on a page of blanks.
        foreach (var source in RatingSources.All)
        {
            if (normalized == CatalogueSortFields.ForRating(source.Source))
            {
                return $"COALESCE({alias}.{source.ScoreColumn}, -1)";
            }
        }

        return normalized switch
        {
            // The stored sort title, not lower(title): "The Matrix" files under
            // M, the way Radarr and Sonarr both do. A column rather than an
            // expression because an expression index only serves an ORDER BY
            // that matches it character for character -- see V0021/V0022.
            CatalogueSortFields.Title => $"COALESCE({alias}.sort_title, lower({alias}.title))",
            CatalogueSortFields.Year => $"COALESCE({alias}.{yearColumn}, -1)",
            CatalogueSortFields.Rating => $"COALESCE({alias}.rating, -1)",
            // Both of these have had an index since V0011/V0012 and neither has
            // ever been offered as a sort — the same shape as the codec and
            // release-group columns the list displayed for months without
            // anything populating them.
            CatalogueSortFields.Runtime => $"COALESCE({alias}.runtime_minutes, -1)",
            CatalogueSortFields.Popularity => $"COALESCE({alias}.popularity, -1)",
            // The picked file's facts, kept on the entry by a trigger (V0016 /
            // V0017) precisely so these two can be an index walk rather than a
            // scan of the whole catalogue.
            CatalogueSortFields.Size => $"COALESCE({alias}.primary_file_size_bytes, -1)",
            CatalogueSortFields.Quality => $"COALESCE({alias}.primary_quality_rank, -1)",
            // Spelled exactly as the expression index in V0016/V0017, because an
            // expression index only serves an ORDER BY that matches it character
            // for character. A stray cast or a reordered COALESCE here turns the
            // page into a sort of the whole catalogue and nothing looks wrong.
            CatalogueSortFields.Bitrate =>
                $"COALESCE(CAST({alias}.primary_file_size_bytes AS REAL) / NULLIF({alias}.runtime_minutes, 0), -1)",
            // A show with nothing still to come sorts last rather than first:
            // "what is on next" is a question about shows that have a next, and
            // burying the answer under every finished series would make the
            // sort useless on any real library.
            CatalogueSortFields.NextAiring =>
                $"COALESCE({alias}.next_air_date_utc, '{CatalogueSortFields.Sentinels.NoNextAiring}')",

            CatalogueSortFields.EpisodeProgress => $"{alias}.aired_with_file_count",

            CatalogueSortFields.Network => $"lower(COALESCE({alias}.network, ''))",

            // Spelled exactly as V0024/V0025 index them. A stray difference
            // here does not break the order, it silently stops using the index
            // and sorts the whole catalogue instead.
            CatalogueSortFields.Monitored => $"{alias}.monitored",
            CatalogueSortFields.Studio => $"lower(COALESCE({alias}.studio, ''))",
            CatalogueSortFields.Certification => $"lower(COALESCE({alias}.certification, ''))",
            CatalogueSortFields.OriginalTitle => $"lower(COALESCE({alias}.original_title, ''))",
            CatalogueSortFields.OriginalLanguage => $"lower(COALESCE({alias}.original_language, ''))",

            // Spelled exactly as CatalogueFileFactsMigrationSql indexes a text
            // fact -- lower(COALESCE(col, '')) -- because an expression index
            // only serves an ORDER BY that matches it character for character.
            // Titles with no file sort together at the top, which is the honest
            // place for them: they have no path.
            CatalogueSortFields.Path => $"lower(COALESCE({alias}.primary_file_path, ''))",

            // A film with no date sorts last rather than first, in both
            // directions: "what is coming out" is a question about films that
            // have a date, and burying the answer under everything already
            // released would make the order useless on a real library.
            CatalogueSortFields.InCinemas => $"COALESCE({alias}.in_cinemas_date, '{CatalogueSortFields.Sentinels.NoDate}')",
            CatalogueSortFields.DigitalRelease => $"COALESCE({alias}.digital_release_date, '{CatalogueSortFields.Sentinels.NoDate}')",
            CatalogueSortFields.PhysicalRelease => $"COALESCE({alias}.physical_release_date, '{CatalogueSortFields.Sentinels.NoDate}')",

            _ => $"{alias}.created_utc"
        };
    }

    /// <summary>
    /// Whether the sort value is a number rather than text, which decides how
    /// the token's value binds. SQLite compares by storage class, so binding a
    /// year as text would silently match nothing.
    /// </summary>
    public static bool IsNumeric(string sortField)
        => RatingSources.All.Any(source =>
               CatalogueSortFields.Normalize(sortField) == CatalogueSortFields.ForRating(source.Source))
        || CatalogueSortFields.Normalize(sortField)
            is CatalogueSortFields.Monitored
            or CatalogueSortFields.Year
            or CatalogueSortFields.Rating
            or CatalogueSortFields.Runtime
            or CatalogueSortFields.Popularity
            or CatalogueSortFields.Size
            or CatalogueSortFields.Quality
            or CatalogueSortFields.Bitrate;

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
        string? upcomingExpression = null,
        string? downloadingExpression = null,
        string? airingExpression = null)
        => CatalogueStatusFilters.Normalize(status) switch
        {
            CatalogueStatusFilters.Downloaded => hasFileExpression,
            // Missing is a *state*, not the absence of a file: a title that is
            // not out yet has no file either, and calling it missing counted it
            // against the library from the day it was added.
            //
            // **A title being downloaded has no file either**, and it was
            // counted twice — once under Downloading and once under Missing — so
            // the chips above the shelf summed to twelve across eleven titles.
            // Invisible until the lab library had anything downloading in it.
            // Same argument as Upcoming, one state along.
            CatalogueStatusFilters.Missing
                when !string.IsNullOrWhiteSpace(upcomingExpression)
                  && !string.IsNullOrWhiteSpace(downloadingExpression)
                => $"NOT {hasFileExpression} AND NOT {upcomingExpression} AND NOT {downloadingExpression}",
            CatalogueStatusFilters.Missing when !string.IsNullOrWhiteSpace(upcomingExpression)
                => $"NOT {hasFileExpression} AND NOT {upcomingExpression}",
            CatalogueStatusFilters.Missing => $"NOT {hasFileExpression}",
            CatalogueStatusFilters.Upgrades when !string.IsNullOrWhiteSpace(upgradeExpression) => upgradeExpression,
            CatalogueStatusFilters.Covered when !string.IsNullOrWhiteSpace(coveredExpression) => coveredExpression,
            CatalogueStatusFilters.Upcoming when !string.IsNullOrWhiteSpace(upcomingExpression) => upcomingExpression,
            CatalogueStatusFilters.Downloading when !string.IsNullOrWhiteSpace(downloadingExpression) => downloadingExpression,
            CatalogueStatusFilters.Airing when !string.IsNullOrWhiteSpace(airingExpression) => airingExpression,
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
    /// The custom narrowing, as one predicate, written once for both catalogues
    /// and driven by <see cref="CatalogueFilterFields"/> rather than by a
    /// hand-written clause per field.
    ///
    /// <para>It used to be nine <c>if</c> blocks here and nine matching binds
    /// below, and the two lists had to agree by hand. Thirty fields would have
    /// been sixty places to keep in step; the registry makes it one row per
    /// question, read by both halves.</para>
    ///
    /// <para>These sit in the WHERE clause beside the search and status filters,
    /// so they narrow the rows an index walk produces rather than changing what
    /// drives it: the page is still a seek on the sort column and still stops as
    /// soon as it has enough rows. A filter nothing matches costs a walk, which
    /// is the honest price of asking a question with no answer.</para>
    ///
    /// <para>Fields declared on the wanted state read <c>ws</c> — the one row
    /// the page speaks for — so a title with no file matches no quality and no
    /// size, which is what a reader means. A title held in two libraries has two
    /// files, and matching on one while displaying the other is the drift the
    /// wanted-state pick was introduced to end.</para>
    ///
    /// <para>Nothing here interpolates a caller's value. Every value is a bound
    /// parameter; the only interpolation is the column expression, which comes
    /// from the registry, and the parameter names, which are generated from a
    /// count.</para>
    /// </summary>
    public static string CustomFilters(CatalogueFilters? filters, MediaKind kind, string alias, string yearColumn)
    {
        if (filters is null || filters.IsEmpty)
        {
            return string.Empty;
        }

        var predicates = new List<string>();

        for (var index = 0; index < filters.Conditions!.Count; index++)
        {
            var condition = filters.Conditions[index];
            var field = CatalogueFilterFields.Find(kind, condition.FieldId);
            if (field is null)
            {
                // Unreachable through the endpoints, which refuse an unknown id
                // with a 400 rather than letting it reach SQL. Skipping here as
                // well means a caller that bypasses them cannot quietly widen a
                // shelf either.
                continue;
            }

            var column = Expression(field, alias, yearColumn);
            var predicate = Predicate(field, condition, column, index);
            if (!string.IsNullOrEmpty(predicate))
            {
                predicates.Add(predicate);
            }
        }

        return predicates.Count == 0 ? string.Empty : string.Join(" AND ", predicates);
    }

    /// <summary>
    /// The registry's column with the catalogue's own names filled in. Two
    /// substitutions, and they are the only difference between the two
    /// catalogues' filter SQL.
    /// </summary>
    private static string Expression(CatalogueFilterField field, string alias, string yearColumn)
        => field.Column.Replace("{alias}", alias).Replace("{year}", yearColumn);

    /// <summary>
    /// Whether a value kind is compared as text, which decides both the
    /// <c>lower()</c> wrapping here and the way <c>IsSet</c> reads an empty
    /// string as absent.
    /// </summary>
    private static bool IsTextual(CatalogueFilterValueKind kind)
        => kind is CatalogueFilterValueKind.Text
            or CatalogueFilterValueKind.Genre
            or CatalogueFilterValueKind.QualityTier
            or CatalogueFilterValueKind.Enum;

    /// <summary>
    /// The one parameter that is not a value somebody typed: the moment the
    /// query is running.
    ///
    /// <para>Named once, because the predicate that reads it and the binder that
    /// fills it live in different methods and a mismatch between them is an
    /// exception at execute time rather than a compile error.</para>
    /// </summary>
    public const string NowParameter = "@filterNow";

    private static string Predicate(
        CatalogueFilterField field,
        CatalogueFilterCondition condition,
        string column,
        int index)
    {
        var textual = IsTextual(field.ValueKind);
        var comparable = textual ? $"lower(COALESCE({column}, ''))" : column;
        var names = condition.Values.Select((_, position) => $"@f{index}v{position}").ToArray();

        switch (condition.Operator)
        {
            case CatalogueFilterOperator.IsSet:
                return textual ? $"COALESCE({column}, '') <> ''" : $"{column} IS NOT NULL";

            case CatalogueFilterOperator.IsNotSet:
                return textual ? $"COALESCE({column}, '') = ''" : $"{column} IS NULL";

            case CatalogueFilterOperator.Includes when field.ValueKind == CatalogueFilterValueKind.Genre:
                return $"({string.Join(" OR ", names.Select(name => GenreMatch(column, name)))})";

            case CatalogueFilterOperator.IncludesAll when field.ValueKind == CatalogueFilterValueKind.Genre:
                // Every genre asked for must be present, because that is what a
                // reader means by picking two.
                return string.Join(" AND ", names.Select(name => GenreMatch(column, name)));

            case CatalogueFilterOperator.Excludes when field.ValueKind == CatalogueFilterValueKind.Genre:
                return $"NOT ({string.Join(" OR ", names.Select(name => GenreMatch(column, name)))})";

            case CatalogueFilterOperator.Includes:
                return $"{comparable} IN ({string.Join(", ", names)})";

            case CatalogueFilterOperator.Excludes:
                return $"{comparable} NOT IN ({string.Join(", ", names)})";

            case CatalogueFilterOperator.Is when field.ValueKind == CatalogueFilterValueKind.Boolean:
            case CatalogueFilterOperator.Is:
                return $"{comparable} = {names[0]}";

            case CatalogueFilterOperator.IsNot:
                return $"{comparable} <> {names[0]}";

            case CatalogueFilterOperator.AtLeast:
                return $"{column} >= {names[0]}";

            case CatalogueFilterOperator.AtMost:
                return $"{column} <= {names[0]}";

            case CatalogueFilterOperator.Contains:
                return $"{comparable} LIKE {names[0]}";

            case CatalogueFilterOperator.DoesNotContain:
                return $"{comparable} NOT LIKE {names[0]}";

            case CatalogueFilterOperator.StartsWith:
            case CatalogueFilterOperator.EndsWith:
                return $"{comparable} LIKE {names[0]}";

            case CatalogueFilterOperator.Before:
                return $"{column} < {names[0]}";

            case CatalogueFilterOperator.After:
                return $"{column} > {names[0]}";

            case CatalogueFilterOperator.WithinLastDays:
                return $"{column} >= {names[0]}";

            case CatalogueFilterOperator.WithinNextDays:
                // Bounded at both ends, and the lower bound is the one shared
                // parameter rather than a second value on the condition: a
                // reader supplies one number and both bounds come off the same
                // clock. Without the lower bound "out on digital in the next
                // fortnight" would also return everything released since 1927.
                return $"({column} >= {NowParameter} AND {column} <= {names[0]})";

            case CatalogueFilterOperator.MoreThanDaysAgo:
                // "or not at all" is the point of this one: "not searched in
                // ninety days" has to include the titles never searched, or the
                // answer omits the worst cases.
                return $"({column} IS NULL OR {column} < {names[0]})";

            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// Whole genres, not substrings: bracketing the list and each term in commas
    /// is what stops "Drama" matching a title tagged "Melodrama".
    /// </summary>
    private static string GenreMatch(string column, string parameterName)
        => $"(',' || replace(lower(COALESCE({column}, '')), ', ', ',') || ',') LIKE {parameterName}";

    /// <summary>
    /// Binds what <see cref="CustomFilters"/> wrote. Kept immediately beside it
    /// for the same reason <c>PageColumns</c> and <c>Read</c> are: two lists
    /// that must agree, in one place, so they can only disagree visibly.
    ///
    /// <para><paramref name="now"/> is the reference point for the relative date
    /// operators. Passed in rather than read here so a test can ask "added in
    /// the last thirty days" of a fixed clock.</para>
    /// </summary>
    public static void BindCustomFilters(
        DbCommand command,
        CatalogueFilters? filters,
        MediaKind kind,
        DateTimeOffset? now = null)
    {
        if (filters is null || filters.IsEmpty)
        {
            return;
        }

        var reference = now ?? DateTimeOffset.UtcNow;

        // Bound whether or not a forward-looking filter is present. An unused
        // parameter costs nothing and SQLite ignores it; a missing one is an
        // exception at execute time, and deciding here whether any condition
        // needs it would be the operator list written in a second place.
        Bind(command, NowParameter, reference.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

        for (var index = 0; index < filters.Conditions!.Count; index++)
        {
            var condition = filters.Conditions[index];
            var field = CatalogueFilterFields.Find(kind, condition.FieldId);
            if (field is null || !CatalogueFilterOperators.TakesValues(condition.Operator))
            {
                continue;
            }

            for (var position = 0; position < condition.Values.Count; position++)
            {
                Bind(command, $"@f{index}v{position}", BoundValue(field, condition, condition.Values[position], reference));
            }
        }
    }

    /// <summary>
    /// One raw string from the query, turned into the thing SQLite has to
    /// compare against.
    ///
    /// <para>SQLite compares by storage class, so a year bound as text silently
    /// matches nothing — the same trap <see cref="BindSeek"/> documents. This is
    /// the one place a value changes shape, which is why the gigabyte-to-byte
    /// conversion and the days-ago arithmetic both live here rather than in the
    /// caller.</para>
    /// </summary>
    private static object BoundValue(
        CatalogueFilterField field,
        CatalogueFilterCondition condition,
        string raw,
        DateTimeOffset now)
    {
        var value = raw.Trim();

        switch (condition.Operator)
        {
            case CatalogueFilterOperator.WithinLastDays:
            case CatalogueFilterOperator.MoreThanDaysAgo:
            case CatalogueFilterOperator.WithinNextDays:
                var days = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDays)
                    ? parsedDays
                    : 0d;
                var offset = condition.Operator == CatalogueFilterOperator.WithinNextDays ? days : -days;
                return now.AddDays(offset).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

            case CatalogueFilterOperator.Contains:
            case CatalogueFilterOperator.DoesNotContain:
                return $"%{value.ToLowerInvariant()}%";

            case CatalogueFilterOperator.StartsWith:
                return $"{value.ToLowerInvariant()}%";

            case CatalogueFilterOperator.EndsWith:
                return $"%{value.ToLowerInvariant()}";
        }

        if (field.ValueKind == CatalogueFilterValueKind.Genre)
        {
            return $"%,{value.ToLowerInvariant()},%";
        }

        return field.ValueKind switch
        {
            CatalogueFilterValueKind.Gigabytes =>
                (long)((double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var gb) ? gb : 0d)
                       * 1024 * 1024 * 1024),
            CatalogueFilterValueKind.Year or CatalogueFilterValueKind.Minutes or CatalogueFilterValueKind.Integer =>
                long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole) ? whole : 0L,
            CatalogueFilterValueKind.Decimal or CatalogueFilterValueKind.Rating =>
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0d,
            CatalogueFilterValueKind.Boolean =>
                value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" ? 1L : 0L,
            CatalogueFilterValueKind.Date => value,
            _ => value.ToLowerInvariant()
        };
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
