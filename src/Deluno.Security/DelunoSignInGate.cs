using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Deluno.Security;

/// <summary>
/// The sign-in check the Windows app puts in front of every API route (#1146).
///
/// <para>It lives here, not in the tray, so it can be tested with a real
/// unauthenticated request. The tray's own copy once asked for a sign-in on
/// everything under /api except a short list of paths, so the poster route
/// (an <c>&lt;img&gt;</c> cannot send a sign-in) answered 401 to everyone and no
/// poster showed on the Windows install. A route that declares itself public
/// with <see cref="DelunoPublicEndpointAttribute"/> is now open here too, so
/// the two cannot drift apart again.</para>
/// </summary>
public static class DelunoSignInGate
{
    /// <summary>
    /// Whether a request needs a signed-in person or key. Public endpoints
    /// (found by their attribute, which needs routing to have run) never do.
    /// </summary>
    public static bool RequiresSignIn(HttpContext context)
    {
        var path = context.Request.Path;

        if (context.GetEndpoint()?.Metadata.GetMetadata<DelunoPublicEndpointAttribute>() is not null)
        {
            return false;
        }

        return (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
                !path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) &&
                !path.Equals("/api/auth/bootstrap-status", StringComparison.OrdinalIgnoreCase) &&
                !path.Equals("/api/auth/bootstrap", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWithSegments("/api/health", StringComparison.OrdinalIgnoreCase)) ||
               path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase);
    }

    public static IApplicationBuilder UseDelunoSignInGate(
        this IApplicationBuilder app,
        Func<PathString, string, string[]> resolveScopes)
    {
        return app.Use(async (context, next) =>
        {
            if (!RequiresSignIn(context))
            {
                await next();
                return;
            }

            var denied = await UserAuthorization.RequireAuthenticatedAsync(context, context.RequestAborted);
            if (denied is not null)
            {
                await denied.ExecuteAsync(context);
                return;
            }

            var scopeDenied = UserAuthorization.RequireApiScope(
                context,
                resolveScopes(context.Request.Path, context.Request.Method));
            if (scopeDenied is not null)
            {
                await scopeDenied.ExecuteAsync(context);
                return;
            }

            await next();
        });
    }
}
