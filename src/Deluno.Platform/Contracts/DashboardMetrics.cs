namespace Deluno.Platform.Contracts;

/// <summary>
/// One day of one measure. Every number here is counted from a stored row with a
/// real timestamp — nothing is smoothed, projected or invented. Days with no
/// activity are present with zero so a chart's x-axis is time, not row order.
/// </summary>
public sealed record MetricPoint(DateOnly Date, int Value);

/// <summary>Two series that only make sense together: attempts and how they went.</summary>
public sealed record MetricOutcomeSeries(
    IReadOnlyList<MetricPoint> Succeeded,
    IReadOnlyList<MetricPoint> Failed);

/// <summary>
/// What the dashboard draws. Four questions: is the library growing, are searches
/// finding anything, is the automation healthy, and is anything getting stuck.
/// </summary>
public sealed record DashboardMetrics(
    int Days,
    DateOnly From,
    DateOnly To,
    /// <summary>Cumulative titles in the library, so the line only ever climbs.</summary>
    IReadOnlyList<MetricPoint> LibrarySize,
    /// <summary>Titles added each day — the growth behind the cumulative line.</summary>
    IReadOnlyList<MetricPoint> TitlesAdded,
    /// <summary>Searches that matched a release versus those that did not.</summary>
    MetricOutcomeSeries Searches,
    /// <summary>Background jobs that completed versus those that failed.</summary>
    MetricOutcomeSeries Jobs,
    /// <summary>Imports that could not be filed, by the day they were detected.</summary>
    IReadOnlyList<MetricPoint> ImportFailures,
    /// <summary>Releases handed to a download client.</summary>
    IReadOnlyList<MetricPoint> Grabs);
