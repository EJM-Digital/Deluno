using System.Globalization;
using Deluno.Platform.Contracts;

namespace Deluno.Platform.Data;

/// <summary>
/// Turning "rows with timestamps" into "a value per day".
///
/// SQLite stores these as ISO strings, so the grouping is a substring — which is
/// exact for UTC and avoids a per-row date parse. Every day in the window is
/// emitted, including the empty ones: a chart that silently skips quiet days
/// compresses time and makes a flat week look busy.
/// </summary>
public static class DailyCounts
{
    public const string GroupExpression = "substr({0}, 1, 10)";

    public static string GroupBy(string column) =>
        string.Format(CultureInfo.InvariantCulture, GroupExpression, column);

    /// <summary>
    /// How many titles failed to import on each day, from a recovery-case table.
    ///
    /// <para>The dashboard's "failed to import" used to be <c>COUNT(*)</c> of
    /// that table, and a recovery case is written for every attempt: one stuck
    /// file retried twelve times was twelve. It also counted cases that never
    /// reached an import at all (a download the client never showed). The rig
    /// read "186 failed to import" beside 15 grabs. A title is counted once per
    /// day, and only for kinds that are an import going wrong.</para>
    /// </summary>
    public static string ImportFailuresByDay(string recoveryTable) =>
        $"SELECT {GroupBy("detected_utc")} AS day, COUNT(DISTINCT lower(title)) FROM {recoveryTable} " +
        "WHERE detected_utc >= @from AND detected_utc < @to " +
        "AND failure_kind <> 'downloadFailed' GROUP BY day;";

    public static IReadOnlyList<MetricPoint> Fill(
        IReadOnlyDictionary<string, int> counts,
        DateOnly from,
        DateOnly to)
    {
        var points = new List<MetricPoint>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            counts.TryGetValue(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out var value);
            points.Add(new MetricPoint(day, value));
        }

        return points;
    }

    /// <summary>
    /// A running total that starts from what already existed before the window,
    /// so the first point is the real library size rather than zero.
    /// </summary>
    public static IReadOnlyList<MetricPoint> Cumulative(IReadOnlyList<MetricPoint> daily, int startingTotal)
    {
        var running = startingTotal;
        var points = new List<MetricPoint>(daily.Count);
        foreach (var point in daily)
        {
            running += point.Value;
            points.Add(new MetricPoint(point.Date, running));
        }

        return points;
    }
}
