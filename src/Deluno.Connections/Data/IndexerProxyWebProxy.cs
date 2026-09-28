using System.Net;
using Deluno.Connections.Contracts;

namespace Deluno.Connections.Data;

/// <summary>
/// A configured indexer proxy (#748) as the <see cref="IWebProxy"/> a
/// <see cref="System.Net.Http.SocketsHttpHandler"/> can route a request
/// through (#976): HTTP and SOCKS4/SOCKS5 are supported natively by
/// <see cref="System.Net.Http.SocketsHttpHandler"/> once its
/// <c>Proxy</c> is set to a socks4/socks4a/socks5-scheme address.
/// </summary>
public static class IndexerProxyWebProxy
{
    /// <summary>
    /// Null for no proxy, a disabled one, or a FlareSolverr/Byparr one --
    /// that kind is a Cloudflare helper Deluno calls, not a forward proxy an
    /// ordinary request can be routed through.
    /// </summary>
    public static IWebProxy? Build(ProxyItem? proxy)
    {
        if (proxy is not { IsEnabled: true } || string.Equals(proxy.ProxyType, ProxyTypes.FlareSolverr, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var address = ProxyAddressFormat.ToUrl(proxy.ProxyType, proxy.Address);
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var webProxy = new WebProxy(uri);
        if (!string.IsNullOrWhiteSpace(proxy.Username))
        {
            webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password ?? string.Empty);
        }

        return webProxy;
    }
}
