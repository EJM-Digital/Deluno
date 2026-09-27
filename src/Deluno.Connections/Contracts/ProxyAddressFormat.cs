namespace Deluno.Connections.Contracts;

/// <summary>
/// A configured proxy's address (#748), read the way a person typed it --
/// with or without a scheme -- normalised into a full <c>scheme://host:port</c>
/// URL a consumer such as the Cloudflare helper (#749) can use directly.
/// </summary>
public static class ProxyAddressFormat
{
    public static string ToUrl(string proxyType, string address)
    {
        var trimmed = address.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        return trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : $"{SchemeFor(proxyType)}://{trimmed}";
    }

    private static string SchemeFor(string proxyType) => ProxyTypes.Normalize(proxyType) switch
    {
        ProxyTypes.Socks4 => "socks4",
        ProxyTypes.Socks5 => "socks5",
        _ => "http"
    };
}
