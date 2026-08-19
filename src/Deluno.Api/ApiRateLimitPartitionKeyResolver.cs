using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Deluno.Api;

/// <summary>
/// Keys the global API rate limiter per caller credential rather than per IP —
/// every caller on a single-machine install shares 127.0.0.1, so an IP-keyed
/// limiter would share one budget across every script. This runs ahead of the
/// auth middleware, so <c>HttpContext.Items["deluno.apiKey"]</c> is not
/// populated yet; it reads the raw credential presented instead. Hashed
/// before use so a raw API key or bearer token never lands in a limiter key,
/// a log line, or an exception.
/// </summary>
public static class ApiRateLimitPartitionKeyResolver
{
    public static string Resolve(HttpContext httpContext)
    {
        var credential = httpContext.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(credential))
        {
            credential = httpContext.Request.Headers.Authorization.FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(credential))
        {
            return httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(credential));
        return Convert.ToHexStringLower(hash);
    }
}
