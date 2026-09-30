using System.Text.Json.Serialization;

namespace Deluno.Connections.Contracts;

/// <summary>
/// A configured indexer proxy (#748): HTTP, SOCKS4, SOCKS5 or FlareSolverr/
/// Byparr, assignable to any number of indexers so different sources can go
/// through different proxies instead of sharing one global Cloudflare-helper
/// address.
/// </summary>
public sealed record ProxyItem(
    string Id,
    string Name,
    string ProxyType,
    string Address,
    string? Username,
    [property: JsonIgnore] string? Password,
    bool IsEnabled,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    /// <summary>The saved secret was stored on another computer, account or key and cannot be read here (#1094). It is listed as missing; entering it again clears this.</summary>
    public bool SecretUnreadable { get; init; }

    /// <summary>Plain-words reason when <see cref="SecretUnreadable"/> is set, for the screen and Health.</summary>
    public string? SecretProblem { get; init; }

    /// <summary>Whether a password is saved, so the screen can say "saved" without seeing it.</summary>
    public bool HasPassword => !string.IsNullOrEmpty(Password);

    /// <summary>Names of the indexers currently assigned to this proxy, filled in by the list endpoint.</summary>
    public IReadOnlyList<string> AssignedIndexerNames { get; init; } = [];
}
