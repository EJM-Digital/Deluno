using System.Globalization;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Deluno.Persistence.Tests.Catalogue;

/// <summary>
/// A relative date filter means what it says on the day it is read, not on the
/// day it was saved.
///
/// <para>This is #308's own closing condition, written as the test it asks for:
/// <i>"a saved view using a relative one returns different rows a month later,
/// and there is a test that fails if the resolution is frozen at save time."</i>
/// The bug it guards against is not a crash — it is a filter that silently
/// stops meaning what it says. "Added in the last 30 days", saved in March and
/// opened in June, would go on returning March's rows for ever, and nothing on
/// screen would look wrong.</para>
///
/// <para>The stored form is a <b>count of days</b>. The absolute instant is
/// computed when the query runs, from the clock passed in — which is why these
/// bind against two different <c>now</c> values and expect two different
/// bounds.</para>
/// </summary>
public sealed class RelativeDateFilterTests
{
    private static readonly DateTimeOffset March = DateTimeOffset.Parse("2026-03-01T00:00:00Z");
    private static readonly DateTimeOffset June = DateTimeOffset.Parse("2026-06-01T00:00:00Z");

    [Fact]
    public void The_same_saved_filter_resolves_to_a_different_instant_three_months_later()
    {
        var saved = Filter("added", CatalogueFilterOperator.WithinLastDays, "30");

        var inMarch = BoundValues(saved, March);
        var inJune = BoundValues(saved, June);

        // If either of these is ever the same, the resolution has been frozen at
        // save time and every saved view in the product is quietly lying.
        Assert.NotEqual(inMarch["@f0v0"], inJune["@f0v0"]);

        Assert.Equal(March.AddDays(-30).UtcDateTime, DateTime.Parse((string)inMarch["@f0v0"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        Assert.Equal(June.AddDays(-30).UtcDateTime, DateTime.Parse((string)inJune["@f0v0"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    /// <summary>
    /// The forward-looking half, which is the one #308 exists for as much as the
    /// backward ones: <i>"what is out on digital next fortnight that I do not
    /// have?"</i>
    /// </summary>
    [Fact]
    public void Within_the_next_n_days_reaches_forwards_from_now_and_not_backwards()
    {
        var upcoming = Filter("digitalRelease", CatalogueFilterOperator.WithinNextDays, "14");

        var bound = BoundValues(upcoming, March);

        Assert.Equal(
            March.AddDays(14).UtcDateTime,
            DateTime.Parse((string)bound["@f0v0"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    /// <summary>
    /// And it is bounded at both ends. Without the lower bound, "out on digital
    /// in the next fortnight" also returns everything released since 1927 —
    /// which is a filter that looks like it works and is wrong on every real
    /// library.
    /// </summary>
    [Fact]
    public void Within_the_next_n_days_is_bounded_at_both_ends()
    {
        var upcoming = Filter("digitalRelease", CatalogueFilterOperator.WithinNextDays, "14");

        var sql = CatalogueKeyset.CustomFilters(upcoming, MediaKind.Movie, "m", "m.year");

        Assert.Contains(">=", sql, StringComparison.Ordinal);
        Assert.Contains("<=", sql, StringComparison.Ordinal);
        Assert.Contains(CatalogueKeyset.NowParameter, sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The lower bound is a parameter the binder has to fill. A predicate naming
    /// one the binder does not supply is an exception at execute time rather
    /// than a compile error, so it is asserted rather than assumed — and it is
    /// bound whether or not any condition wants it.
    /// </summary>
    [Fact]
    public void The_clock_is_always_bound_so_a_predicate_can_never_name_a_parameter_that_is_missing()
    {
        var unrelated = Filter("added", CatalogueFilterOperator.WithinLastDays, "30");

        Assert.True(BoundValues(unrelated, March).ContainsKey(CatalogueKeyset.NowParameter));
    }

    /// <summary>
    /// "Not searched in ninety days" has to include the never-searched, or the
    /// answer omits the worst cases — the titles quietly stuck in a retry loop,
    /// which are the whole reason somebody asks.
    /// </summary>
    [Fact]
    public void Beyond_n_days_includes_what_was_never_touched_at_all()
    {
        var stale = Filter("lastSearch", CatalogueFilterOperator.MoreThanDaysAgo, "90");

        var sql = CatalogueKeyset.CustomFilters(stale, MediaKind.Movie, "m", "m.year");

        Assert.Contains("IS NULL", sql, StringComparison.Ordinal);
    }

    private static CatalogueFilters Filter(string field, CatalogueFilterOperator op, string value)
        => new([new CatalogueFilterCondition(field, op, [value])]);

    /// <summary>
    /// What the binder actually put on the command, which is the only place the
    /// resolution can be observed.
    /// </summary>
    private static Dictionary<string, object> BoundValues(CatalogueFilters filters, DateTimeOffset now)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        using var command = connection.CreateCommand();

        CatalogueKeyset.BindCustomFilters(command, filters, MediaKind.Movie, now);

        return command.Parameters
            .Cast<SqliteParameter>()
            .ToDictionary(parameter => parameter.ParameterName, parameter => parameter.Value!);
    }
}
