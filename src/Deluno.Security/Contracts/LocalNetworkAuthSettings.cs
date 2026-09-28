namespace Deluno.Security.Contracts;

/// <summary>
/// Skip sign-in for devices on the local network (#993): whether a request
/// that Deluno can prove came from a private, loopback or link-local address
/// -- never one merely claimed by an untrusted forwarded-for header -- is
/// treated as already signed in. Off is what every earlier release did,
/// which is why a brand-new install still starts here (nothing turns on by
/// itself, #936) until setup or the Security page turns it on.
/// </summary>
/// <param name="Enabled">Whether the skip applies at all.</param>
/// <param name="AdditionalLocalNetworks">
/// Subnets (CIDR or single IP) the person added themselves, counted as local
/// in addition to RFC1918, loopback, link-local and IPv6 ULA.
/// </param>
/// <param name="AllowList">
/// When not empty, narrows which local addresses actually skip sign-in to
/// just these (CIDR or single IP) -- still only if they are also local.
/// Empty means the local-network rule applies to every local address, as
/// designed.
/// </param>
/// <param name="TrustedProxies">
/// The only addresses Deluno will take an X-Forwarded-For hop from when
/// deciding whether a request is local. A request relayed through anything
/// else is judged on the connection's own address instead, so a reverse
/// proxy outside this list can never manufacture a "local" request for an
/// outside caller.
/// </param>
public sealed record LocalNetworkAuthSettings(
    bool Enabled,
    IReadOnlyList<string> AdditionalLocalNetworks,
    IReadOnlyList<string> AllowList,
    IReadOnlyList<string> TrustedProxies)
{
    public static readonly LocalNetworkAuthSettings Off = new(false, [], [], []);
}

/// <summary>
/// Everything the Security page's "Skip sign-in for devices on your home
/// network" card needs in one call: the saved choice, and the address
/// answering this very request -- with whether it would be trusted -- so the
/// page can explain the feature without anyone needing to know a CIDR.
/// </summary>
public sealed record LocalNetworkAuthOverview(
    bool Enabled,
    IReadOnlyList<string> AdditionalLocalNetworks,
    IReadOnlyList<string> AllowList,
    IReadOnlyList<string> TrustedProxies,
    string CurrentAddress,
    bool CurrentAddressIsLocal,
    bool CurrentAddressTrusted,
    bool AllowedHostsEnabled);

public sealed record UpdateLocalNetworkAuthRequest(
    bool Enabled,
    IReadOnlyList<string> AdditionalLocalNetworks,
    IReadOnlyList<string> AllowList,
    IReadOnlyList<string> TrustedProxies);
