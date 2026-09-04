using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace Deluno.Security;

/// <summary>
/// Says why a request was refused.
///
/// <para>ASP.NET's default handler writes a bare <c>403</c> — no body, no
/// content type. For a browser that is survivable, because the UI knows what it
/// asked for. For an API key it is not: keys are used by scripts, and the author
/// of a failing script could not tell whether the key was wrong, revoked, out of
/// scope, or pointed at a route no key can reach.</para>
///
/// <para>Deluno explains itself everywhere else — <em>"WEB 2160p is not one of
/// the qualities this profile allows"</em>, <em>"qBittorrent does not have a
/// category named …"</em> — and the API surface should not be the one place
/// that answers with silence.</para>
/// </summary>
public sealed class DelunoAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Forbidden || context.Response.HasStarted)
        {
            await _default.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        var required = policy.Requirements
            .OfType<DelunoScopeRequirement>()
            .SelectMany(requirement => requirement.Scopes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var granted = context.User.Claims
            .Where(claim => claim.Type == DelunoAuthenticationHandler.ScopeClaimType)
            .Select(claim => claim.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (required.Length == 0)
        {
            await _default.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";

        var scopeList = string.Join("' or '", required);
        var error = granted.Length == 0
            ? $"This credential carries no scopes, so it cannot use this route. It needs '{scopeList}'."
            : $"This credential does not have the '{scopeList}' scope.";

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(new
            {
                error,
                required,
                granted
            }),
            context.RequestAborted);
    }
}
