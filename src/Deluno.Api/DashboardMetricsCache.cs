using Deluno.Infrastructure.Caching;
using Deluno.Platform.Contracts;

namespace Deluno.Api;

/// <summary>
/// The dashboard's time series, kept for thirty seconds (#1081). It counts stored
/// rows by the day they carry, three databases wide, and a day's bars do not move
/// second by second; the dashboard asks again every time it is opened.
/// </summary>
public sealed class DashboardMetricsCache(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    public GenerationMemo<DashboardMetrics> Memo { get; } = new(clock);
}
