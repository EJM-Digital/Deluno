using Deluno.Contracts;
using Deluno.Security.Data;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// Says when "skip sign-in for devices on the local network" (#993) is on --
/// the same plain-words rule every automatic behaviour follows -- and warns
/// if Allowed Hosts is off at the same time, since together that combination
/// answers on any address AND treats a local one as already signed in.
/// </summary>
public sealed class LocalNetworkAuthHealthCheck(
    ILocalNetworkAuthRepository repository,
    IAllowedHostsRepository allowedHostsRepository) : IHealthCheck
{
    public const string Id = "security:local-network-auth";
    private const string Name = "Skip sign-in for local devices";

    public string Category => HealthCheckCategories.System;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var settings = await repository.GetAsync(cancellationToken);
        if (!settings.Enabled)
        {
            return [HealthCheckOutcome.Ok(Id, Category, Name, "Off. Every device signs in, including ones on your local network.")];
        }

        var allowedHosts = await allowedHostsRepository.GetAsync(cancellationToken);
        if (!allowedHosts.Enabled)
        {
            return [HealthCheckOutcome.Problem(
                Id, Category, Name,
                "Devices on your local network skip sign-in, and Allowed Hosts is off.",
                cause: "With Allowed Hosts off, Deluno answers a request no matter which address it was sent to -- combined with this setting, a device that can reach it on a local address never needs the password.",
                fix: "Open Security and turn on Allowed Hosts, or turn this off.")];
        }

        var narrowed = settings.AllowList.Count > 0
            ? $" Narrowed to {settings.AllowList.Count} chosen address{(settings.AllowList.Count == 1 ? "" : "es")}."
            : string.Empty;
        return [HealthCheckOutcome.Ok(Id, Category, Name, $"On. Devices on your local network skip sign-in.{narrowed}")];
    }
}
