using System.Text;
using System.Text.Json;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Platform;

public static class UserAuthorization
{
    private const string UserItemKey = "deluno.user";
    private const string ApiKeyItemKey = "deluno.apiKey";
    private const string UserTokenPurpose = "Deluno.UserAccessToken.v1";

    public static string IssueAccessToken(IDataProtectionProvider dataProtectionProvider, UserItem item)
    {
        var payload = new UserTokenPayload(
            item.Id,
            item.Username,
            item.DisplayName,
            item.AvatarInitials,
            item.CreatedUtc);

        var protector = dataProtectionProvider.CreateProtector(UserTokenPurpose);
        return protector.Protect(JsonSerializer.Serialize(payload));
    }

    public static async ValueTask<IResult?> RequireAuthenticatedAsync(
        HttpContext httpContext,
        IPlatformSettingsRepository repository,
        CancellationToken cancellationToken)
    {
        var apiKey = ReadApiKey(httpContext);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var currentApiKey = await repository.ValidateApiKeyAsync(apiKey, cancellationToken);
            if (currentApiKey is not null)
            {
                httpContext.Items[ApiKeyItemKey] = currentApiKey;
                return null;
            }
        }

        if (!TryReadUser(httpContext, out var authenticated) || authenticated is null)
        {
            return Results.Unauthorized();
        }

        var current = await repository.GetUserByIdAsync(authenticated.Id, cancellationToken);
        if (current is null)
        {
            return Results.Unauthorized();
        }

        httpContext.Items[UserItemKey] = current;
        return null;
    }

    public static bool TryReadApiKey(HttpContext httpContext, out ApiKeyItem? item)
    {
        item = null;
        if (httpContext.Items.TryGetValue(ApiKeyItemKey, out var value) && value is ApiKeyItem apiKey)
        {
            item = apiKey;
            return true;
        }

        return false;
    }

    public static IResult? RequireApiScope(HttpContext httpContext, params string[] requiredScopes)
    {
        if (!TryReadApiKey(httpContext, out var apiKey) || apiKey is null)
        {
            return null;
        }

        if (ApiKeyHasAnyScope(apiKey, requiredScopes))
        {
            return null;
        }

        return Results.Json(
            new
            {
                message = $"This API key does not include the required scope: {string.Join(" or ", requiredScopes)}."
            },
            statusCode: StatusCodes.Status403Forbidden);
    }

    public static bool ApiKeyHasAnyScope(ApiKeyItem apiKey, params string[] requiredScopes)
    {
        var scopes = apiKey.Scopes
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(scope => scope.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (scopes.Contains("all") || scopes.Contains("*"))
        {
            return true;
        }

        return requiredScopes.Any(scope => scopes.Contains(scope.ToLowerInvariant()));
    }

    public static bool TryReadUser(HttpContext httpContext, out UserItem? item)
    {
        item = null;
        var token = ReadToken(httpContext);
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var protector = httpContext.RequestServices
                .GetRequiredService<IDataProtectionProvider>()
                .CreateProtector(UserTokenPurpose);
            var json = protector.Unprotect(token);
            var payload = JsonSerializer.Deserialize<UserTokenPayload>(json);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Id) || string.IsNullOrWhiteSpace(payload.Username))
            {
                return false;
            }

            item = new UserItem(
                payload.Id,
                payload.Username,
                payload.DisplayName,
                payload.AvatarInitials,
                payload.CreatedUtc);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadToken(HttpContext httpContext)
    {
        var header = httpContext.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(header) &&
            header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearerToken = header["Bearer ".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                return bearerToken;
            }
        }

        if (httpContext.Request.Query.TryGetValue("access_token", out var accessTokenValues))
        {
            var queryToken = accessTokenValues.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(queryToken))
            {
                return queryToken;
            }
        }

        return null;
    }

    private static string? ReadApiKey(HttpContext httpContext)
    {
        var explicitHeader = httpContext.Request.Headers["X-Api-Key"].ToString().Trim();
        if (IsApiKeyToken(explicitHeader))
        {
            return explicitHeader;
        }

        var header = httpContext.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(header) &&
            header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearerToken = header["Bearer ".Length..].Trim();
            if (IsApiKeyToken(bearerToken))
            {
                return bearerToken;
            }
        }

        return null;
    }

    private static bool IsApiKeyToken(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.StartsWith("deluno_", StringComparison.OrdinalIgnoreCase);

    private sealed record UserTokenPayload(
        string Id,
        string Username,
        string DisplayName,
        string AvatarInitials,
        DateTimeOffset CreatedUtc);
}
