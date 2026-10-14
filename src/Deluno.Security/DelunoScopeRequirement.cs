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
        var http = httpContextAccessor.HttpContext;
        var method = http?.Request.Method ?? string.Empty;
        // #1314: a system-scoped policy asks for system on every method; a read-only key must not list keys, backups or update settings.
        var isRead = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
        var requiredScopes = isRead && !requirement.Scopes.SequenceEqual(["system"])
            // #1265: the two reads a processor makes for its hand-off work also open to the imports scope; every other read asks for read.
            ? (http?.GetEndpoint()?.Metadata.GetMetadata<DelunoImportsScopeMayReadAttribute>() is not null
                ? new[] { "read", "imports" }
                : new[] { "read" })
            : requirement.Scopes;

        if (requiredScopes.Any(scope => context.User.HasClaim(DelunoAuthenticationHandler.ScopeClaimType, scope)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Marks a GET route that a key with the <c>imports</c> scope may also read (#1265). A GET otherwise asks for <c>read</c>. It
/// is metadata on a named route, never a wildcard: a test walks every GET route and fails if an imports-only key gets through
/// anywhere else.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class DelunoImportsScopeMayReadAttribute : Attribute;
