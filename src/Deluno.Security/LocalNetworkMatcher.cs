using System.Net;
using System.Net.Sockets;

namespace Deluno.Security;

/// <summary>
/// The address classification behind "skip sign-in for devices on the local
/// network" (#993): RFC1918, loopback and link-local IPv4, IPv6 ULA and
/// link-local, plus whatever subnets a person adds themselves. Shared by the
/// enforcement check and the Security page overview so the two can never
/// disagree about what counts as local.
/// </summary>
public static class LocalNetworkMatcher
{
    private static readonly (IPAddress Network, int PrefixLength)[] BuiltInLocalNetworks =
    [
        // IPv4 loopback (127.0.0.0/8), RFC1918 private ranges, and link-local (169.254.0.0/16).
        (IPAddress.Parse("127.0.0.0"), 8),
        (IPAddress.Parse("10.0.0.0"), 8),
        (IPAddress.Parse("172.16.0.0"), 12),
        (IPAddress.Parse("192.168.0.0"), 16),
        (IPAddress.Parse("169.254.0.0"), 16),
        // IPv6 loopback (::1/128), unique local addresses (fc00::/7), and link-local (fe80::/10).
        (IPAddress.Parse("::1"), 128),
        (IPAddress.Parse("fc00::"), 7),
        (IPAddress.Parse("fe80::"), 10)
    ];

    /// <summary>
    /// Whether <paramref name="address"/> is one of the built-in local ranges
    /// or one of the person's own <paramref name="additionalNetworks"/>
    /// (CIDR or bare IP, one per entry). An address that fails to parse never
    /// counts as local.
    /// </summary>
    public static bool IsLocal(IPAddress address, IEnumerable<string> additionalNetworks)
    {
        var mapped = Unmap(address);

        foreach (var (network, prefixLength) in BuiltInLocalNetworks)
        {
            if (IsInSubnet(mapped, network, prefixLength))
            {
                return true;
            }
        }

        return MatchesAny(mapped, additionalNetworks);
    }

    /// <summary>
    /// Whether <paramref name="address"/> falls inside any of
    /// <paramref name="networks"/> (CIDR or bare IP). Used both for the
    /// person's own added local networks and for the allow list and trusted
    /// proxies, which share the same CIDR-or-IP entry format.
    /// </summary>
    public static bool MatchesAny(IPAddress address, IEnumerable<string> networks)
    {
        var mapped = Unmap(address);
        foreach (var entry in networks)
        {
            if (TryParseCidr(entry, out var network, out var prefixLength) &&
                IsInSubnet(mapped, network, prefixLength))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses a single IP ("192.168.1.20") or CIDR ("192.168.1.0/28",
    /// "fd00::/8") entry. A bare address is treated as an exact match
    /// (/32 for IPv4, /128 for IPv6).
    /// </summary>
    public static bool TryParseCidr(string? entry, out IPAddress network, out int prefixLength)
    {
        network = IPAddress.None;
        prefixLength = 0;

        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        var trimmed = entry.Trim();
        var slash = trimmed.IndexOf('/');
        var addressPart = slash < 0 ? trimmed : trimmed[..slash];

        if (!IPAddress.TryParse(addressPart, out var parsedAddress))
        {
            return false;
        }

        parsedAddress = Unmap(parsedAddress);
        var maxPrefix = parsedAddress.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;

        if (slash < 0)
        {
            network = parsedAddress;
            prefixLength = maxPrefix;
            return true;
        }

        var prefixPart = trimmed[(slash + 1)..];
        if (!int.TryParse(prefixPart, out var parsedPrefix) || parsedPrefix < 0 || parsedPrefix > maxPrefix)
        {
            return false;
        }

        network = parsedAddress;
        prefixLength = parsedPrefix;
        return true;
    }

    /// <summary>Whether an entry Deluno was asked to save is a valid IP or CIDR.</summary>
    public static bool IsValidEntry(string? entry) => TryParseCidr(entry, out _, out _);

    public static bool IsInSubnet(IPAddress address, IPAddress network, int prefixLength)
    {
        var mappedAddress = Unmap(address);
        var mappedNetwork = Unmap(network);

        if (mappedAddress.AddressFamily != mappedNetwork.AddressFamily)
        {
            return false;
        }

        var addressBytes = mappedAddress.GetAddressBytes();
        var networkBytes = mappedNetwork.GetAddressBytes();
        if (prefixLength < 0 || prefixLength > addressBytes.Length * 8)
        {
            return false;
        }

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (addressBytes[i] != networkBytes[i])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)~(0xFF >> remainingBits);
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }

    /// <summary>An IPv4-mapped IPv6 address ("::ffff:192.168.1.20") is the same client as the plain IPv4 one.</summary>
    private static IPAddress Unmap(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
