using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Deluno.Api;

/// <summary>
/// Decides whether the global API rate limiter applies to a request at all,
/// and if so, what to partition it by.
///
/// This limiter exists for the surface #142 was filed about: a scoped API
/// key held by a third-party script or integration, which can misbehave in
/// ways Deluno's own code never does — a retry loop, a bug, a genuinely
/// hostile caller. It is not meant to throttle Deluno's own UI or its
/// internal jobs, which are trusted and already bounded by other means (the
/// worker's lane batching and backstop intervals, the dashboard's own
/// polling cadence). A request presenting the browser session's own access
/// token — not a generated <c>deluno_</c>-prefixed API key — is exempt.
/// Every open browser tab of the same login otherwise shares one partition
/// (they all present the same token), which made ordinary multi-tab use of
/// Deluno itself compete with the budget meant for external scripts.
///
/// Runs ahead of the auth middleware, so <c>HttpContext.Items["deluno.apiKey"]</c>
/// is not populated yet; this reads the raw credential presented instead,
/// mirroring <c>UserAuthorization.ReadApiKey</c>'s "X-Api-Key header, else a
/// deluno_-prefixed bearer token" rule without depending on Deluno.Security.
/// </summary>
public static class ApiRateLimitPartitionKeyResolver
{
    private const string ApiKeyPrefix = "deluno_";

    /// <summary>
    /// Returns the partition key for a request the limiter should apply to,
    /// or <c>null</c> when the request is exempt (a browser session token,
    /// not a generated API key).
    /// </summary>
    public static string? ResolveOrExempt(HttpContext httpContext)
    {
        var apiKey = ReadApiKeyCredential(httpContext);
        if (apiKey is not null)
        {
            return Hash(apiKey);
        }

        var hasSessionToken = !string.IsNullOrWhiteSpace(
            httpContext.Request.Headers["X-Api-Key"].FirstOrDefault()) ||
            !string.IsNullOrWhiteSpace(httpContext.Request.Headers.Authorization.FirstOrDefault());
        if (hasSessionToken)
        {
            // Presented a credential, but not a deluno_-prefixed API key --
            // the browser's own session token. Trusted, exempt.
            return null;
        }

        // No credential at all. Most /api routes require authentication, so
        // this is narrow (health checks, login/bootstrap) -- fall back to
        // IP-based limiting rather than exempting anonymous traffic outright.
        return httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private static string? ReadApiKeyCredential(HttpContext httpContext)
    {
        var explicitHeader = httpContext.Request.Headers["X-Api-Key"].FirstOrDefault()?.Trim();
        if (IsApiKeyToken(explicitHeader))
        {
            return explicitHeader;
        }

        var authorization = httpContext.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(authorization) &&
            authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearerToken = authorization["Bearer ".Length..].Trim();
            if (IsApiKeyToken(bearerToken))
            {
                return bearerToken;
            }
        }

        return null;
    }

    private static bool IsApiKeyToken(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.StartsWith(ApiKeyPrefix, StringComparison.OrdinalIgnoreCase);

    private static string Hash(string credential)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
}
