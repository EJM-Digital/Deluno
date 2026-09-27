using System.Net;

namespace Deluno.Security;

/// <summary>
/// The matching rule behind Allowed Hosts (#939), shared by the enforcement
/// middleware and the save-time lock-out check so the two can never disagree
/// about what a saved list actually allows.
/// </summary>
public static class AllowedHostMatcher
{
    /// <summary>
    /// "localhost", "127.0.0.1" and "::1" always answer, whatever is saved
    /// -- and whatever is left unticked -- so a person can always reach
    /// Deluno from the machine it runs on to fix a mistake here. This checks
    /// the literal address the request named, not the socket it arrived on:
    /// a DNS-rebinding page connects over loopback too, but it does so with
    /// an attacker's hostname in the Host header, not "localhost", so it
    /// still needs to be on the saved list to get through.
    /// </summary>
    public static bool IsAlwaysAllowedLoopbackHost(string host)
    {
        var bare = StripPort(host);
        if (bare.Length == 0) return false;
        if (bare.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (bare.StartsWith('[') && bare.EndsWith(']'))
        {
            bare = bare[1..^1];
        }

        return IPAddress.TryParse(bare, out var address) && IPAddress.IsLoopback(address);
    }

    /// <summary>
    /// Whether <paramref name="host"/> matches one of the saved
    /// <paramref name="patterns"/>: an exact, case-insensitive match, or a
    /// <c>*.example.com</c> wildcard matching any subdomain of
    /// <c>example.com</c> (not the bare domain itself -- add that separately
    /// if it should also answer).
    /// </summary>
    public static bool Matches(string host, IEnumerable<string> patterns)
    {
        var bare = StripPort(host);
        if (bare.Length == 0) return false;

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            var trimmed = pattern.Trim();

            if (trimmed.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = trimmed[1..]; // ".example.com"
                if (bare.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && bare.Length > suffix.Length)
                {
                    return true;
                }
                continue;
            }

            if (string.Equals(bare, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Removes a trailing <c>:port</c>, but leaves an IPv6 literal's own brackets alone.</summary>
    public static string StripPort(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;
        var value = host.Trim();

        if (value.StartsWith('['))
        {
            var closing = value.IndexOf(']');
            return closing < 0 ? value : value[..(closing + 1)];
        }

        var colon = value.IndexOf(':');
        return colon < 0 ? value : value[..colon];
    }
}
