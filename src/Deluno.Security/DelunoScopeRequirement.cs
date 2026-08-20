using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Deluno.Security;

public sealed class DelunoScopeRequirement(params string[] scopes) : IAuthorizationRequirement
{
    public IReadOnlyList<string> Scopes { get; } = scopes;
}

public sealed class DelunoScopeAuthorizationHandler(IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<DelunoScopeRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        DelunoScopeRequirement requirement)
    {
        var method = httpContextAccessor.HttpContext?.Request.Method ?? string.Empty;
        var requiredScopes = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)
            ? new[] { "read" }
            : requirement.Scopes;

        if (requiredScopes.Any(scope => context.User.HasClaim(DelunoAuthenticationHandler.ScopeClaimType, scope)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
