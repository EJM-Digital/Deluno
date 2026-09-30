namespace Deluno.Connections.Contracts;

/// <summary>
/// The proxy kinds an Indexer Proxy (#748) can be: the same four Prowlarr
/// offers on its own Indexer Proxies page.
/// </summary>
public static class ProxyTypes
{
    public const string Http = "http";
    public const string Socks4 = "socks4";
    public const string Socks5 = "socks5";
    public const string FlareSolverr = "flaresolverr";

    public static string Normalize(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "http" => Http,
            "socks4" => Socks4,
            "socks5" => Socks5,
            "flaresolverr" or "byparr" => FlareSolverr,
            _ => Http
        };
    }

    /// <summary>The kind as it reads in a proxy's automatic name (#1076).</summary>
    public static string DisplayName(string? value) => Normalize(value) switch
    {
        Socks4 => "SOCKS4 proxy",
        Socks5 => "SOCKS5 proxy",
        FlareSolverr => "FlareSolverr",
        _ => "HTTP proxy"
    };

    /// <summary>
    /// A proxy's automatic name (#1076): "FlareSolverr on RIG", "SOCKS5 proxy on 192.168.1.9".
    /// The address may carry a socks5:// or socks4:// scheme, which the shared naming rules
    /// do not read, so only its host and port are handed on. Also returns the port, which
    /// tells two proxies of one kind on one host apart.
    /// </summary>
    public static (string Name, int? Port) AutomaticName(string? proxyType, string? address)
    {
        var hostAndPort = address?.Trim();
        if (Uri.TryCreate(hostAndPort, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            var host = uri.Host.Contains(':') ? $"[{uri.Host.Trim('[', ']')}]" : uri.Host;
            hostAndPort = uri.Port > 0 ? $"http://{host}:{uri.Port}" : $"http://{host}";
        }

        return (Deluno.Contracts.ConnectionNames.AppOnHost(DisplayName(proxyType), hostAndPort), Deluno.Contracts.ConnectionNames.PortOf(hostAndPort));
    }

    public static bool IsKnown(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Http or Socks4 or Socks5 or FlareSolverr => true,
        _ => false
    };
}
