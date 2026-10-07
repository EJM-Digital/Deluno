using System.Text;
using System.Text.Json;
using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Security;

public static class UserAuthorization
{
    internal const string UserItemKey = "deluno.user";
    internal const string ApiKeyItemKey = "deluno.apiKey";
    private const string UserTokenPurpose = "Deluno.UserAccessToken.v1";

    /// <summary>
    /// #1165: a session lasts 7 days, or 30 when the person ticked "Keep me
    /// signed in on this device". Either one renews itself while in use: a
    /// request that finds the token past halfway through its life gets a fresh
    /// token back in <see cref="RenewedTokenHeader"/>.
    /// </summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan KeepSignedInLifetime = TimeSpan.FromDays(30);

    public const string RenewedTokenHeader = "X-Deluno-Access-Token";
    public const string RenewedTokenExpiresHeader = "X-Deluno-Access-Token-Expires";

    public static LoginResponse IssueLoginResponse(
        IDataProtectionProvider dataProtectionProvider,
        TimeProvider timeProvider,
        UserItem item,
        bool keepSignedIn = false)
    {
        var issuedUtc = timeProvider.GetUtcNow();
        var expiresUtc = issuedUtc.Add(keepSignedIn ? KeepSignedInLifetime : SessionLifetime);
        return new LoginResponse(
            AccessToken: IssueAccessToken(dataProtectionProvider, item, issuedUtc, expiresUtc),
            ExpiresUtc: expiresUtc,
            User: item);
    }

    public static string IssueAccessToken(
        IDataProtectionProvider dataProtectionProvider,
        UserItem item,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc)
    {
        var payload = new UserTokenPayload(
            item.Id,
            item.Username,
            item.DisplayName,
            item.AvatarInitials,
            item.SecurityStamp,
            issuedUtc,
            expiresUtc,
            item.CreatedUtc);

        var protector = dataProtectionProvider.CreateProtector(UserTokenPurpose);
        return protector.Protect(JsonSerializer.Serialize(payload));
    }

    /// <summary>
    /// Authenticates the caller using the request's own service scope.
    ///
    /// Endpoints used to take an IPlatformSettingsRepository purely to hand it
    /// to this method, which is why that interface ended up injected across
    /// nearly every endpoint in the app (ADR-001). Resolving ISecurityRepository
    /// here instead keeps auth a one-argument concern; the explicit overload
    /// below stays for tests that supply their own repository.
    /// </summary>
    public static ValueTask<IResult?> RequireAuthenticatedAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken)
        => RequireAuthenticatedAsync(
            httpContext,
            httpContext.RequestServices.GetRequiredService<ISecurityRepository>(),
            cancellationToken);

    public static async ValueTask<IResult?> RequireAuthenticatedAsync(
        HttpContext httpContext,
        ISecurityRepository repository,
        CancellationToken cancellationToken)
    {
        if (TryReadApiKey(httpContext, out _) || TryReadUserItem(httpContext, out _))
        {
            return null;
        }

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
            // #993: a request Deluno can prove arrived from a local network
            // address -- never one merely claimed by a forwarded-for header
            // from an untrusted source -- is treated as already signed in,
            // but only once the setting is on (off by default, #936).
            if (await TryLocalNetworkAuthAsync(httpContext, repository, cancellationToken))
            {
                return null;
            }

            return Results.Unauthorized();
        }

        var current = await repository.GetUserByIdAsync(authenticated.Id, cancellationToken);
        if (current is null)
        {
            return Results.Unauthorized();
        }

        if (!string.Equals(current.SecurityStamp, authenticated.SecurityStamp, StringComparison.Ordinal))
        {
            return Results.Unauthorized();
        }

        httpContext.Items[UserItemKey] = current;
        TryRenewToken(httpContext, current);
        return null;
    }

    /// <summary>
    /// #1165: sliding renewal. When the presented token has used more than half
    /// of its life, hand back a fresh one with the same length so a page that
    /// is open or in use never reaches the end of its session. The security
    /// stamp rides in the new token, so a password change or sign-out still
    /// ends every session.
    /// </summary>
    private static void TryRenewToken(HttpContext httpContext, UserItem current)
    {
        try
        {
            var token = ReadToken(httpContext);
            if (string.IsNullOrWhiteSpace(token) || IsApiKeyToken(token) || httpContext.Response.HasStarted)
            {
                return;
            }

            var provider = httpContext.RequestServices.GetRequiredService<IDataProtectionProvider>();
            var json = provider.CreateProtector(UserTokenPurpose).Unprotect(token);
            var payload = JsonSerializer.Deserialize<UserTokenPayload>(json);
            if (payload is null)
            {
                return;
            }

            var timeProvider = httpContext.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
            var now = timeProvider.GetUtcNow();
            var lifetime = payload.ExpiresUtc - payload.IssuedUtc;
            if (lifetime <= TimeSpan.Zero || payload.ExpiresUtc - now > lifetime / 2)
            {
                return;
            }

            var expiresUtc = now.Add(lifetime);
            httpContext.Response.Headers[RenewedTokenHeader] = IssueAccessToken(provider, current, now, expiresUtc);
            httpContext.Response.Headers[RenewedTokenExpiresHeader] = expiresUtc.ToString("O");
        }
        catch
        {
            // Renewal is a convenience; a failure must never fail the request.
        }
    }

    /// <summary>
    /// #993: whether this request should be treated as signed in because it
    /// came from a local network address, with skip-sign-in turned on. Every
    /// check fails safe -- missing services, an unresolved address, or the
    /// setting being off all mean "no" -- so this can never widen access by
    /// accident.
    /// </summary>
    private static async Task<bool> TryLocalNetworkAuthAsync(
        HttpContext httpContext,
        ISecurityRepository repository,
        CancellationToken cancellationToken)
    {
        var settingsCache = httpContext.RequestServices.GetService<Hardening.ILocalNetworkAuthSettingsCache>();
        if (settingsCache is null)
        {
            return false;
        }

        var settings = await settingsCache.GetAsync(cancellationToken);
        if (!settings.Enabled)
        {
            return false;
        }

        var resolver = httpContext.RequestServices.GetService<Hardening.ITrustedProxyClientAddressResolver>();
        var address = resolver is null
            ? httpContext.Connection.RemoteIpAddress
            : await resolver.ResolveAsync(httpContext, cancellationToken);
        if (address is null)
        {
            return false;
        }

        if (!LocalNetworkMatcher.IsLocal(address, settings.AdditionalLocalNetworks))
        {
            return false;
        }

        // An empty allow list means the local-network rule applies to every
        // local address, as designed; a non-empty one narrows it further --
        // still only for an address that is also local.
        if (settings.AllowList.Count > 0 && !LocalNetworkMatcher.MatchesAny(address, settings.AllowList))
        {
            return false;
        }

        var user = await repository.GetSoleUserAsync(cancellationToken);
        if (user is null)
        {
            return false;
        }

        httpContext.Items[UserItemKey] = user;
        return true;
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
        if (TryReadUserItem(httpContext, out item))
        {
            return true;
        }

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

            var timeProvider = httpContext.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
            if (payload.ExpiresUtc <= timeProvider.GetUtcNow())
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(payload.SecurityStamp))
            {
                return false;
            }

            item = new UserItem(
                payload.Id,
                payload.Username,
                payload.DisplayName,
                payload.AvatarInitials,
                payload.SecurityStamp,
                payload.CreatedUtc);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadUserItem(HttpContext httpContext, out UserItem? item)
    {
        item = null;
        if (httpContext.Items.TryGetValue(UserItemKey, out var value) && value is UserItem user)
        {
            item = user;
            return true;
        }

        return false;
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

        // WebSocket connections (SignalR) cannot send arbitrary headers, so the
        // client sends the token as ?access_token=... in the upgrade URL instead.
        var queryToken = httpContext.Request.Query["access_token"].ToString().Trim();
        if (!string.IsNullOrWhiteSpace(queryToken))
        {
            return queryToken;
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
        string SecurityStamp,
        DateTimeOffset IssuedUtc,
        DateTimeOffset ExpiresUtc,
        DateTimeOffset CreatedUtc);
}
