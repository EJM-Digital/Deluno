using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Security;
using Deluno.Series.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Api;

/// <summary>
/// The dashboard's time series.
///
/// This lives here rather than in Platform because it is the one endpoint that
/// has to read across both media engines and the job queue, and Platform is
/// underneath all three. Everything it returns is a count of stored rows grouped
/// by the day in their own timestamp — there is no sampling, smoothing or
/// projection anywhere in it, which is the whole point: the sparklines this
/// replaces were hardcoded arrays.
/// </summary>
public static class DashboardMetricsEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoDashboardMetricsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/dashboard/metrics", async (
            int? days,
            HttpContext httpContext,
            [FromServices] IPlatformSettingsRepository platformSettingsRepository,
            [FromServices] IMovieCatalogRepository movieCatalogRepository,
            [FromServices] ISeriesCatalogRepository seriesCatalogRepository,
            [FromServices] IJobQueueRepository jobQueueRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var window = Math.Clamp(days ?? 30, 7, 365);
            var to = DateOnly.FromDateTime(DateTime.UtcNow);
            var from = to.AddDays(-(window - 1));

            var movies = await movieCatalogRepository.GetDailyMetricsAsync(from, to, cancellationToken);
            var series = await seriesCatalogRepository.GetDailyMetricsAsync(from, to, cancellationToken);
            var jobs = await jobQueueRepository.GetDailyMetricsAsync(from, to, cancellationToken);

            // Movies and TV are separate engines but one library, so they add up.
            var titlesAdded = DailyCounts.Fill(Merge(movies.TitlesAdded, series.TitlesAdded), from, to);

            return Results.Ok(new DashboardMetrics(
                Days: window,
                From: from,
                To: to,
                LibrarySize: DailyCounts.Cumulative(titlesAdded, movies.TitlesBeforeWindow + series.TitlesBeforeWindow),
                TitlesAdded: titlesAdded,
                Searches: new MetricOutcomeSeries(
                    DailyCounts.Fill(Merge(movies.SearchesMatched, series.SearchesMatched), from, to),
                    DailyCounts.Fill(Merge(movies.SearchesUnmatched, series.SearchesUnmatched), from, to)),
                Jobs: new MetricOutcomeSeries(
                    DailyCounts.Fill(jobs.JobsCompleted, from, to),
                    DailyCounts.Fill(jobs.JobsFailed, from, to)),
                ImportFailures: DailyCounts.Fill(Merge(movies.ImportFailures, series.ImportFailures), from, to),
                Grabs: DailyCounts.Fill(jobs.Grabs, from, to)));
        });

        return endpoints;
    }

    /// <summary>Add two per-day maps together, day by day.</summary>
    private static IReadOnlyDictionary<string, int> Merge(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right)
    {
        var merged = new Dictionary<string, int>(left, StringComparer.Ordinal);
        foreach (var (day, value) in right)
        {
            merged[day] = merged.TryGetValue(day, out var existing) ? existing + value : value;
        }

        return merged;
    }
}
