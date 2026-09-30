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

        return endpoints;
    }
}
