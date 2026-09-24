using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Api.Network;

/// <summary>
/// Whether other devices on the network can reach this Deluno (#623).
///
/// <para>On Windows that is a firewall question: Deluno listens on every
/// interface, and the firewall drops what comes in on its port unless a rule
/// lets it through. Adding a rule needs an administrator, and a normal install
/// has none, so the rule was never there. The only trace was a log file, and
/// another device just failed to connect.</para>
/// </summary>
public interface ILanAccess
{
    Task<LanAccessStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Opens Deluno's port to the network: directly when Deluno already runs as
    /// an administrator, through the Windows permission prompt on this computer
    /// when it does not.
    /// </summary>
    Task<LanAccessStatus> AllowAsync(CancellationToken cancellationToken);
}

public static class LanAccessStates
{
    /// <summary>The firewall lets other devices reach Deluno's port.</summary>
    public const string Open = "open";

    /// <summary>The firewall has no rule for Deluno's port, so other devices are turned away.</summary>
    public const string Blocked = "blocked";

    /// <summary>Nothing here for Deluno to check: not Windows, or Deluno only listens on this computer.</summary>
    public const string NotChecked = "notChecked";
}

public sealed record LanAccessStatus(
    string State,
    int? Port,
    string? RuleName,
    string Message,
    bool CanAllow);

/// <summary>A host with no firewall for Deluno to look after: Docker, Linux, or a loopback-only Deluno.</summary>
public sealed class UncheckedLanAccess : ILanAccess
{
    private static readonly LanAccessStatus Status = new(
        LanAccessStates.NotChecked,
        null,
        null,
        "Deluno does not manage a firewall on this host.",
        CanAllow: false);

    public Task<LanAccessStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Status);

    public Task<LanAccessStatus> AllowAsync(CancellationToken cancellationToken) => Task.FromResult(Status);
}

public static class LanAccessEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoLanAccessEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/system/lan-access", async (ILanAccess lanAccess, CancellationToken cancellationToken)
                => Results.Ok(await lanAccess.GetStatusAsync(cancellationToken)))
            .RequireAuthorization(DelunoAuthorizationPolicies.Read);

        api.MapPost("/system/lan-access/allow", async (ILanAccess lanAccess, CancellationToken cancellationToken)
                => Results.Ok(await lanAccess.AllowAsync(cancellationToken)))
            .RequireAuthorization(DelunoAuthorizationPolicies.System);

        return api;
    }
}
