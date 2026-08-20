using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Security;

/// <summary>
/// /api/auth and /api/api-keys. Split out of
/// PlatformEndpointRouteBuilderExtensions by ADR-001 Step 1; handler bodies
/// are unchanged apart from the repository type and an explicit
/// [FromServices], which minimal APIs need or they infer a body parameter.
/// </summary>
public static class SecurityEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoSecurityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/auth");

        // Only /login carries the limiter. bootstrap-status is polled by the
        // web app on load, so limiting the whole group would throttle normal
        // use rather than credential guessing.
        var login = endpoints.MapGroup("/api/auth")
            .RequireRateLimiting(DelunoRateLimitPolicies.Login);
        var apiKeys = endpoints.MapGroup("/api/api-keys")
            .RequireAuthorization(DelunoAuthorizationPolicies.System);

        login.MapPost("/login", async (
            [FromBody] LoginRequest request,
            IDataProtectionProvider dataProtectionProvider,
            TimeProvider timeProvider,
            [FromServices] ISecurityRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["username"] = ["Username is required."],
                    ["password"] = ["Password is required."]
                });
            }

            var login = await repository.ValidateUserCredentialsAsync(request.Username, request.Password, cancellationToken);
            if (login is null)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(UserAuthorization.IssueLoginResponse(dataProtectionProvider, timeProvider, login));
        })
            .AllowAnonymous()
            .WithMetadata(new DelunoPublicEndpointAttribute());

        auth.MapGet("/bootstrap-status", async (
            [FromServices] ISecurityRepository repository,
            CancellationToken cancellationToken) =>
        {
            var requiresSetup = await repository.RequiresBootstrapAsync(cancellationToken);
            return Results.Ok(new BootstrapStatusResponse(RequiresSetup: requiresSetup));
        })
            .AllowAnonymous()
            .WithMetadata(new DelunoPublicEndpointAttribute());

        auth.MapPost("/bootstrap", async (
            [FromBody] BootstrapUserRequest request,
            IDataProtectionProvider dataProtectionProvider,
            TimeProvider timeProvider,
            [FromServices] ISecurityRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (!await repository.RequiresBootstrapAsync(cancellationToken))
            {
                return Results.Conflict(new
                {
                    message = "Deluno has already been configured."
                });
            }

            var errors = ValidateBootstrap(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var created = await repository.BootstrapUserAsync(request, cancellationToken);
            return Results.Ok(UserAuthorization.IssueLoginResponse(dataProtectionProvider, timeProvider, created));
        })
            .AllowAnonymous()
            .WithMetadata(new DelunoPublicEndpointAttribute());

        auth.MapPost("/logout", async (
            HttpContext httpContext,
            [FromServices] ISecurityRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (!UserAuthorization.TryReadUser(httpContext, out var user) || user is null)
            {
                return Results.Unauthorized();
            }

            await repository.RevokeUserAccessTokensAsync(user.Id, cancellationToken);
            return Results.NoContent();
        })
            .RequireAuthorization(DelunoAuthorizationPolicies.Read);

        auth.MapPut("/password", async (
            HttpContext httpContext,
            [FromBody] ChangePasswordRequest request,
            [FromServices] ISecurityRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (!UserAuthorization.TryReadUser(httpContext, out var user) || user is null)
            {
                return Results.Unauthorized();
            }

            var errors = ValidatePasswordChange(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var changed = await repository.ChangeUserPasswordAsync(
                user.Id,
                request.CurrentPassword!,
                request.NewPassword!,
                cancellationToken);

            if (!changed)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["currentPassword"] = ["Current password is not correct."]
                });
            }

            return Results.NoContent();
        })
            .RequireAuthorization(DelunoAuthorizationPolicies.Read);

        apiKeys.MapGet(string.Empty, async (
            HttpContext httpContext,
            [FromServices] ISecurityRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var items = await repository.ListApiKeysAsync(cancellationToken);
            return Results.Ok(items);
        });

        apiKeys.MapPost(string.Empty, async (
            HttpContext httpContext,
            [FromBody] CreateApiKeyRequest request,
            [FromServices] ISecurityRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["Give this API key a clear name."]
                });
            }

            var created = await repository.CreateApiKeyAsync(request, cancellationToken);
            return Results.Ok(created);
        });

        apiKeys.MapDelete("{id}", async (
            string id,
            HttpContext httpContext,
            [FromServices] ISecurityRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteApiKeyAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        return endpoints;
    }

    private static Dictionary<string, string[]> ValidateUser(string? username, string? password)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(username))
        {
            errors["username"] = ["Give this user a username."];
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            errors["password"] = ["Give this user a password."];
        }
        else if (password.Length < 8)
        {
            errors["password"] = ["Use at least 8 characters for the password."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateBootstrap(BootstrapUserRequest request)
    {
        var errors = ValidateUser(request.Username, request.Password);

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            errors["displayName"] = ["Choose the name Deluno should show in the app."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidatePasswordChange(ChangePasswordRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.CurrentPassword))
        {
            errors["currentPassword"] = ["Enter your current password."];
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword))
        {
            errors["newPassword"] = ["Enter a new password."];
        }
        else if (request.NewPassword.Length < 8)
        {
            errors["newPassword"] = ["Use at least 8 characters for the new password."];
        }

        if (!string.IsNullOrWhiteSpace(request.CurrentPassword) &&
            !string.IsNullOrWhiteSpace(request.NewPassword) &&
            string.Equals(request.CurrentPassword, request.NewPassword, StringComparison.Ordinal))
        {
            errors["newPassword"] = ["Choose a password that is different from your current password."];
        }

        return errors;
    }

}
