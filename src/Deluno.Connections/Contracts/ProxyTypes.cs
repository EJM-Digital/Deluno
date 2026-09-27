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

    public static bool IsKnown(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Http or Socks4 or Socks5 or FlareSolverr => true,
        _ => false
    };
}
