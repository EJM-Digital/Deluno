using System.Security.Claims;
using System.Text.Encodings.Web;
using Deluno.Security.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Deluno.Security;

public sealed class DelunoAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Deluno";
    public const string ScopeClaimType = "deluno:scope";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var denied = await UserAuthorization.RequireAuthenticatedAsync(Context, Context.RequestAborted);
        if (denied is not null)
        {
            return AuthenticateResult.NoResult();
        }

        if (UserAuthorization.TryReadApiKey(Context, out var apiKey) && apiKey is not null)
        {
            return Success(CreateTicket(
                apiKey.Id,
                apiKey.Name,
                "api-key",
                ExpandScopes(apiKey)));
        }

        if (UserAuthorization.TryReadUser(Context, out var user) && user is not null)
        {
            // UI access tokens have historically been unscoped. Keep that
            // contract while endpoint policies make API-key scopes explicit.
            return Success(CreateTicket(
                user.Id,
                user.Username,
                "user",
                DelunoAuthorizationPolicies.AllScopes));
        }

        return AuthenticateResult.NoResult();
    }

    private AuthenticationTicket CreateTicket(
        string id,
        string name,
        string callerType,
        IEnumerable<string> scopes)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, id),
            new(ClaimTypes.Name, name),
            new("deluno:caller-type", callerType)
        };
        claims.AddRange(scopes.Select(scope => new Claim(ScopeClaimType, scope)));
        return new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName);
    }

    private static IReadOnlyList<string> ExpandScopes(ApiKeyItem apiKey)
    {
        var scopes = apiKey.Scopes
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(scope => scope.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return scopes.Contains("all") || scopes.Contains("*")
            ? DelunoAuthorizationPolicies.AllScopes
            : scopes.ToArray();
    }

    private static AuthenticateResult Success(AuthenticationTicket ticket)
        => AuthenticateResult.Success(ticket);
}
