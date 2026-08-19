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
                rest.StartsWithSegments($"/{DelunoApiVersion.Current}", out var withoutVersion))
            {
                context.Request.Path = "/api" + withoutVersion;
            }
            else if (path.StartsWithSegments("/api", out var other) &&
                     Regex.IsMatch(other.Value ?? string.Empty, "^/v[0-9]+(/|$)"))
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
}
