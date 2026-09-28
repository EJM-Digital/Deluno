using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Deluno.Security.Hardening;

public static class CaptureOriginalRemoteAddressMiddlewareExtensions
{
    /// <summary>
    /// Must run before <c>UseForwardedHeaders</c> (#993): that middleware
    /// overwrites <c>Connection.RemoteIpAddress</c> from X-Forwarded-For
    /// unconditionally (Deluno's KnownProxies/KnownNetworks are cleared, see
    /// Program.cs), so the real socket peer has to be stashed before that
    /// happens. Without it, "skip sign-in for local devices" would trust
    /// whatever any caller claims in that header, defeating the trusted-proxy
    /// check entirely.
    /// </summary>
    public static IApplicationBuilder UseDelunoCaptureOriginalRemoteAddress(this IApplicationBuilder app)
        => app.Use((context, next) =>
        {
            context.Items[LocalNetworkAuthConstants.OriginalRemoteAddressItemKey] = context.Connection.RemoteIpAddress;
            return next(context);
        });
}

public static class LocalNetworkAuthConstants
{
    public const string OriginalRemoteAddressItemKey = "deluno.originalRemoteAddress";
}

/// <summary>
/// Resolves the address a request should be judged on for "skip sign-in for
/// local devices" (#993): the real socket peer, walked one X-Forwarded-For
/// hop at a time only while each hop it trusts is itself a configured
/// trusted proxy. A caller sitting behind anything else -- including one
/// that forges the header to claim it came via a trusted proxy while its own
/// connection did not -- is judged on its own connection address, never on
/// a header it controls.
/// </summary>
public interface ITrustedProxyClientAddressResolver
{
    Task<IPAddress?> ResolveAsync(HttpContext context, CancellationToken cancellationToken);
}

public sealed class TrustedProxyClientAddressResolver(ILocalNetworkAuthSettingsCache settingsCache)
    : ITrustedProxyClientAddressResolver
{
    public async Task<IPAddress?> ResolveAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var originalAddress = context.Items.TryGetValue(LocalNetworkAuthConstants.OriginalRemoteAddressItemKey, out var stored) && stored is IPAddress captured
            ? captured
            : context.Connection.RemoteIpAddress;

        if (originalAddress is null)
        {
            return null;
        }

        var settings = await settingsCache.GetAsync(cancellationToken);
        if (settings.TrustedProxies.Count == 0)
        {
            return originalAddress;
        }

        if (!LocalNetworkMatcher.MatchesAny(originalAddress, settings.TrustedProxies))
        {
            // The socket that actually connected to Deluno is not a
            // configured trusted proxy, so any X-Forwarded-For it sent is
            // ignored outright -- a spoofed header from an untrusted source
            // never changes the resolved address.
            return originalAddress;
        }

        var header = context.Request.Headers["X-Forwarded-For"].ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return originalAddress;
        }

        // Rightmost hop is nearest to Deluno. Walk leftward, one trusted
        // proxy hop at a time, stopping at the first hop that is not itself
        // a trusted proxy -- that one is the real client.
        var hops = header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var resolved = originalAddress;
        for (var i = hops.Length - 1; i >= 0; i--)
        {
            if (!IPAddress.TryParse(AllowedHostMatcher.StripPort(hops[i]), out var hopAddress))
            {
                break;
            }

            resolved = hopAddress;
            if (!LocalNetworkMatcher.MatchesAny(hopAddress, settings.TrustedProxies))
            {
                break;
            }
        }

        return resolved;
    }
}
