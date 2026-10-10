using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Worker.Dashboard;

public static class DashboardSummaryEndpoints
{
    /// <summary>
    /// <c>GET /api/dashboard/summary</c>: everything the Dashboard shows, in one
    /// response (#1082). Mapped inside the Read policy group. Built from stored
    /// rows and in-memory readings only, and remembered for a few seconds, so it
    /// answers quickly however many screens ask (#1081).
    /// </summary>
    public static IEndpointRouteBuilder MapDelunoDashboardSummaryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/dashboard/summary", async (
            IDashboardSummaryService summaries,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var summary = await summaries.GetAsync(cancellationToken);
            // The web refetches on a realtime nudge; a browser or proxy must not answer that from its own copy.
            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.Ok(summary);
        }).WithName("Get Dashboard Summary");

        // What the Live screen shows beyond the summary (#1096): today's arrivals
        // and upgrades, what was refused, Seerr requests in flight and what
        // retention will give back. Stored rows only, remembered for five seconds.
        endpoints.MapGet("/api/dashboard/live-extras", async (
            string? sinceUtc,
            bool? fresh,
            DashboardLiveService live,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var now = timeProvider.GetUtcNow();
            // The caller says when its day began; never more than 48 hours back.
            var since = DateTimeOffset.TryParse(sinceUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : now.AddHours(-24);
            if (since < now.AddHours(-48)) since = now.AddHours(-48);
            // Seconds only, so the same day is one cache entry however the caller spells it.
            since = new DateTimeOffset(since.UtcTicks - (since.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
            return Results.Ok(await live.GetAsync(since, cancellationToken, fresh == true));
        }).WithName("Get Dashboard Live Extras");

        return endpoints;
    }
}
