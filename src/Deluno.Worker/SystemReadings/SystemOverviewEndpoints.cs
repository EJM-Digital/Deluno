using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Worker.SystemReadings;

public static class SystemOverviewEndpoints
{
    /// <summary>
    /// <c>GET /api/system/overview</c>: everything the System screen shows about Deluno and the machine it
    /// runs on (#1098), read live and remembered for about two seconds so any number of screens cost one
    /// reading. The same shape is pushed as <c>SystemOverviewChanged</c> while a screen is watching.
    /// Mapped inside the Read policy group.
    /// </summary>
    public static IEndpointRouteBuilder MapDelunoSystemOverviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/system/overview", async (
            SystemOverviewService overview,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await overview.GetAsync(cancellationToken));
        }).WithName("Get System Overview");

        return endpoints;
    }
}
