using System.Text;
using System.Text.Json;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.AspNetCore.Http;

namespace Deluno.Platform;

public static class UserAuthorization
{
    private const string UserItemKey = "deluno.user";

    public static string IssueAccessToken(UserItem item)
    {
        var payload = new UserTokenPayload(
            item.Id,
            item.Username,
            item.DisplayName,
            item.AvatarInitials,
            item.CreatedUtc);

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
    }

    public static async ValueTask<IResult?> RequireAuthenticatedAsync(
        HttpContext httpContext,
        IPlatformSettingsRepository repository,
        CancellationToken cancellationToken)
    {
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
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(token));
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

    private sealed record UserTokenPayload(
        string Id,
        string Username,
        string DisplayName,
        string AvatarInitials,
        DateTimeOffset CreatedUtc);
}
