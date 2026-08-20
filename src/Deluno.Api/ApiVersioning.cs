using System.Text.RegularExpressions;
using Deluno.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Deluno.Api;

public static class ApiVersioning
{
    /// <summary>
    /// Accepts <c>/api/{version}/...</c> as an alias for <c>/api/...</c>, and
    /// rejects any other version explicitly with 400 rather than letting it
    /// fall through to a 404 like an unrecognised route. Every response
    /// (aliased, rejected, or plain) carries <c>X-Deluno-Api-Version</c>.
    ///
    /// Must run before the rate limiter, so it partitions on the canonical
    /// path, and before any auth middleware whose allowlist compares exact
    /// paths such as "/api/auth/login" — those would treat an un-rewritten
    /// "/api/v1/auth/login" as requiring authentication.
    /// </summary>
    public static IApplicationBuilder UseDelunoApiVersioning(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments("/api", out var rest) &&
                rest.StartsWithSegments($"/{DelunoApiVersion.Current}", out var withoutVersion) &&
                !HasDedicatedVersionedRoute(rest))
            {
                context.Request.Path = "/api" + withoutVersion;
            }
            else if (path.StartsWithSegments("/api", out var other) &&
                     Regex.IsMatch(other.Value ?? string.Empty, "^/v[0-9]+(/|$)") &&
                     !HasDedicatedVersionedRoute(other))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    $"{{\"error\":\"Unsupported API version. This build serves {DelunoApiVersion.Current}.\"}}");
                return;
            }

            context.Response.Headers["X-Deluno-Api-Version"] = DelunoApiVersion.Current;
            await next();
        });
    }

    // These operational endpoints deliberately use a versioned route because
    // their unversioned names are already occupied by legacy dashboard data.
    // Keep their canonical /api/v1 path intact; other v1 routes remain aliases
    // for the unversioned API above.
    private static bool HasDedicatedVersionedRoute(PathString path)
        => path.StartsWithSegments("/v1/download-dispatches", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/v1/import-resolutions", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/v1/dispatch-alerts", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/v1/dispatch-metrics", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/v1/import-recovery", StringComparison.OrdinalIgnoreCase);
}
