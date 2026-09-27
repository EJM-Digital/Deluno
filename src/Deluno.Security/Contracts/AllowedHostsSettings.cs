namespace Deluno.Security.Contracts;

/// <summary>
/// Allowed Hosts (#939): whether Deluno checks the address a request was sent
/// to, and which addresses it accepts while it does. Off means what every
/// earlier release did -- answer on any address -- which is why a brand-new
/// install still starts here (nothing turns on by itself, #936) until the
/// setup guide or Security page turns it on.
/// </summary>
public sealed record AllowedHostsSettings(bool Enabled, IReadOnlyList<string> AllowedHosts)
{
    public static readonly AllowedHostsSettings Off = new(false, []);
}

/// <summary>
/// One address Deluno noticed it could be reached on, and the plain-English
/// reason it is on the list -- so ticking one never requires knowing what a
/// hostname or a reverse proxy is.
/// </summary>
public sealed record DetectedHostCandidate(string Host, string Reason);

/// <summary>
/// Everything the Security page's Allowed Hosts card needs in one call: the
/// saved choice, what Deluno can see right now, and the address answering
/// this very request -- so the page can warn before a save would lock the
/// person out of it.
/// </summary>
public sealed record AllowedHostsOverview(
    bool Enabled,
    IReadOnlyList<string> AllowedHosts,
    IReadOnlyList<DetectedHostCandidate> DetectedHosts,
    string CurrentHost);

public sealed record UpdateAllowedHostsRequest(bool Enabled, IReadOnlyList<string> AllowedHosts);
